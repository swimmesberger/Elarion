//! Native Typst engine for `Elarion.Typst.Native`.
//!
//! The C ABI is deliberately small and data-only:
//!
//! - Opaque handles (`Engine`, `CompileResult`) with explicit create/destroy functions.
//! - Inputs are borrowed `(pointer, length)` byte views ([`ElarionBytes`]) that the caller keeps alive for the
//!   duration of the call; nothing is retained after a call returns.
//! - Outputs are owned by a result handle and stay valid until it is destroyed, so .NET can read the PDF without a
//!   copy and free it deterministically.
//! - Every export catches panics: unwinding across the FFI boundary is undefined behaviour, so a Typst bug becomes an
//!   error status instead of a crashed host.
//! - Strings are UTF-8, diagnostics are a JSON array (one allocation, trivially versioned).
//!
//! An engine is created once per process. It loads fonts once and caches package files across compilations; each
//! compilation gets its own in-memory project (main file, data, assets) and runs on the caller's thread.

use std::collections::HashMap;
use std::panic::{AssertUnwindSafe, catch_unwind};
use std::path::PathBuf;
use std::sync::Mutex;

use serde::Serialize;
use typst::diag::{FileError, FileResult, PackageError, Severity, SourceDiagnostic, Warned};
use typst::foundations::{Bytes, Datetime, Dict, Duration, IntoValue, Smart};
use typst_layout::PagedDocument;
use typst::syntax::{FileId, RootedPath, Source, VirtualPath, VirtualRoot};
use typst::text::{Font, FontBook};
use typst::utils::LazyHash;
use typst::{Library, LibraryExt, World, WorldExt};
use typst_kit::datetime::Time;
use typst_kit::fonts::{self, FontStore};
use typst_kit::packages::FsPackages;
use typst_pdf::{PdfOptions, PdfStandard, PdfStandards, Timestamp};

/// Bumped on every breaking change of the exported functions or structs; .NET refuses a mismatching library.
pub const ABI_VERSION: u32 = 1;

pub const STATUS_OK: i32 = 0;
pub const STATUS_COMPILE_ERROR: i32 = 1;
pub const STATUS_INVALID_ARGUMENT: i32 = 2;
pub const STATUS_PANIC: i32 = 3;

/// A borrowed byte view. An empty view may carry a null pointer.
#[repr(C)]
#[derive(Clone, Copy)]
pub struct ElarionBytes {
    pub ptr: *const u8,
    pub len: usize,
}

impl ElarionBytes {
    const EMPTY: Self = Self { ptr: std::ptr::null(), len: 0 };

    fn of(slice: &[u8]) -> Self {
        Self { ptr: slice.as_ptr(), len: slice.len() }
    }

    /// # Safety
    /// `ptr` must be valid for `len` bytes for the lifetime `'a`.
    unsafe fn as_slice<'a>(self) -> &'a [u8] {
        if self.len == 0 { &[] } else { unsafe { std::slice::from_raw_parts(self.ptr, self.len) } }
    }

    /// # Safety
    /// See [`ElarionBytes::as_slice`].
    unsafe fn as_str<'a>(self) -> Result<&'a str, String> {
        std::str::from_utf8(unsafe { self.as_slice() }).map_err(|_| "a string argument is not valid UTF-8".to_string())
    }
}

/// A project file: its absolute virtual path (`/data.json`) and its content.
#[repr(C)]
pub struct ElarionTypstFile {
    pub path: ElarionBytes,
    pub content: ElarionBytes,
}

/// One `sys.inputs` entry.
#[repr(C)]
pub struct ElarionTypstInput {
    pub key: ElarionBytes,
    pub value: ElarionBytes,
}

#[repr(C)]
pub struct ElarionTypstEngineOptions {
    pub font_paths: *const ElarionBytes,
    pub font_paths_len: usize,
    /// Local package directory (`--package-path`); empty for none.
    pub package_path: ElarionBytes,
    /// Package cache directory (`--package-cache-path`); empty for none. Never downloaded into.
    pub package_cache_path: ElarionBytes,
    pub include_system_fonts: u8,
}

#[repr(C)]
pub struct ElarionTypstCompileOptions {
    /// The text of `/main.typ`.
    pub main: ElarionBytes,
    pub files: *const ElarionTypstFile,
    pub files_len: usize,
    pub inputs: *const ElarionTypstInput,
    pub inputs_len: usize,
    /// Comma-separated CLI names (`a-2b,ua-1`); empty for Typst's default.
    pub pdf_standards: ElarionBytes,
    pub creation_timestamp: i64,
    pub has_creation_timestamp: u8,
    pub tagged: u8,
}

/// Process-wide engine state: fonts and the package file cache.
pub struct Engine {
    fonts: FontStore,
    packages: Vec<FsPackages>,
    package_files: Mutex<HashMap<FileId, FileResult<Bytes>>>,
    package_sources: Mutex<HashMap<FileId, Source>>,
}

/// The outcome of one compilation, owned by the caller until [`elarion_typst_result_destroy`].
pub struct CompileResult {
    pdf: Vec<u8>,
    diagnostics: String,
}

#[derive(Serialize)]
struct Diagnostic {
    severity: &'static str,
    message: String,
    file: Option<String>,
    line: Option<usize>,
    column: Option<usize>,
    hints: Vec<String>,
    trace: Vec<String>,
}

/// One compilation's world: the shared engine plus this request's in-memory project.
struct RequestWorld<'a> {
    engine: &'a Engine,
    library: LazyHash<Library>,
    main: FileId,
    files: HashMap<FileId, Bytes>,
    sources: Mutex<HashMap<FileId, Source>>,
    now: Time,
}

impl Engine {
    fn new(options: &ElarionTypstEngineOptions) -> Result<Self, String> {
        let mut fonts = FontStore::new();
        if options.include_system_fonts != 0 {
            fonts.extend(fonts::system());
        }

        fonts.extend(fonts::embedded());
        for path in unsafe { slice_of(options.font_paths, options.font_paths_len) } {
            let path = unsafe { path.as_str() }?;
            fonts.extend(fonts::scan(std::path::Path::new(path)));
        }

        let mut packages = Vec::new();
        for directory in [options.package_path, options.package_cache_path] {
            let directory = unsafe { directory.as_str() }?;
            if !directory.is_empty() {
                packages.push(FsPackages::new(PathBuf::from(directory)));
            }
        }

        Ok(Self {
            fonts,
            packages,
            package_files: Mutex::new(HashMap::new()),
            package_sources: Mutex::new(HashMap::new()),
        })
    }

    /// Package files are read once and kept: the template tree is read-only for the life of the process, so the
    /// second document no longer touches the disk and comemo can reuse the parsed package modules.
    fn package_file(&self, id: FileId) -> FileResult<Bytes> {
        if let Some(cached) = self.package_files.lock().unwrap().get(&id) {
            return cached.clone();
        }

        let VirtualRoot::Package(spec) = id.root() else { return Err(FileError::AccessDenied) };
        let loaded = self
            .packages
            .iter()
            .find_map(|packages| packages.obtain(spec))
            .ok_or_else(|| FileError::Package(PackageError::NotFound(spec.clone())))
            .and_then(|root| root.load(id.vpath()));
        self.package_files.lock().unwrap().insert(id, loaded.clone());
        loaded
    }

    fn package_source(&self, id: FileId) -> FileResult<Source> {
        if let Some(source) = self.package_sources.lock().unwrap().get(&id) {
            return Ok(source.clone());
        }

        let bytes = self.package_file(id)?;
        let text = std::str::from_utf8(&bytes).map_err(|_| FileError::InvalidUtf8)?;
        let source = Source::new(id, text.to_string());
        self.package_sources.lock().unwrap().insert(id, source.clone());
        Ok(source)
    }
}

impl World for RequestWorld<'_> {
    fn library(&self) -> &LazyHash<Library> {
        &self.library
    }

    fn book(&self) -> &LazyHash<FontBook> {
        self.engine.fonts.book()
    }

    fn main(&self) -> FileId {
        self.main
    }

    fn source(&self, id: FileId) -> FileResult<Source> {
        if matches!(id.root(), VirtualRoot::Package(_)) {
            return self.engine.package_source(id);
        }

        if let Some(source) = self.sources.lock().unwrap().get(&id) {
            return Ok(source.clone());
        }

        let bytes = self.file(id)?;
        let text = std::str::from_utf8(&bytes).map_err(|_| FileError::InvalidUtf8)?;
        let source = Source::new(id, text.to_string());
        self.sources.lock().unwrap().insert(id, source.clone());
        Ok(source)
    }

    fn file(&self, id: FileId) -> FileResult<Bytes> {
        match id.root() {
            VirtualRoot::Package(_) => self.engine.package_file(id),
            VirtualRoot::Project => self
                .files
                .get(&id)
                .cloned()
                .ok_or_else(|| FileError::NotFound(PathBuf::from(id.vpath().get_with_slash()))),
        }
    }

    fn font(&self, index: usize) -> Option<Font> {
        self.engine.fonts.font(index)
    }

    fn today(&self, offset: Option<Duration>) -> Option<Datetime> {
        self.now.today(offset)
    }
}

fn project_file(path: &str) -> Result<FileId, String> {
    let vpath = VirtualPath::new(path).map_err(|_| format!("'{path}' is not a valid project path"))?;
    Ok(RootedPath::new(VirtualRoot::Project, vpath).intern())
}

fn compile(engine: &Engine, options: &ElarionTypstCompileOptions) -> Result<(i32, CompileResult), String> {
    let main_text = unsafe { options.main.as_str() }?;
    let main = project_file("/main.typ")?;

    let mut files = HashMap::new();
    files.insert(main, Bytes::from_string(main_text.to_string()));
    for file in unsafe { slice_of(options.files, options.files_len) } {
        let id = project_file(unsafe { file.path.as_str() }?)?;
        files.insert(id, Bytes::new(unsafe { file.content.as_slice() }.to_vec()));
    }

    let mut inputs = Dict::new();
    for input in unsafe { slice_of(options.inputs, options.inputs_len) } {
        let key = unsafe { input.key.as_str() }?;
        let value = unsafe { input.value.as_str() }?;
        inputs.insert(key.into(), value.into_value());
    }

    let creation = (options.has_creation_timestamp != 0).then_some(options.creation_timestamp);
    let now = match creation {
        Some(timestamp) => Time::fixed_timestamp(timestamp).map_err(|error| error.to_string())?,
        None => Time::system(),
    };

    let standards = parse_standards(unsafe { options.pdf_standards.as_str() }?)?;

    let world = RequestWorld {
        engine,
        library: LazyHash::new(Library::builder().with_inputs(inputs).build()),
        main,
        files,
        sources: Mutex::new(HashMap::new()),
        now,
    };

    let Warned { output, warnings } = typst::compile::<PagedDocument>(&world);
    let mut diagnostics: Vec<Diagnostic> = warnings.iter().map(|d| describe(&world, d)).collect();
    let document = match output {
        Ok(document) => document,
        Err(errors) => {
            diagnostics.extend(errors.iter().map(|d| describe(&world, d)));
            return Ok((STATUS_COMPILE_ERROR, finish(Vec::new(), &diagnostics)));
        }
    };

    let pdf_options = PdfOptions {
        ident: Smart::Auto,
        creator: Smart::Auto,
        timestamp: timestamp(creation),
        page_ranges: None,
        standards,
        tagged: options.tagged != 0,
        pretty: false,
    };

    // Typst memoizes across compilations (comemo) in one process-wide cache. Evicting entries unused for a few
    // compilations keeps a long-lived process bounded while the shared package modules stay warm. Measured trade-off
    // (ADR-0094): a shorter age lowers memory and lock contention, a longer one speeds up repeated large layouts.
    let result = match typst_pdf::pdf(&document, &pdf_options) {
        Ok(pdf) => (STATUS_OK, finish(pdf, &diagnostics)),
        Err(errors) => {
            diagnostics.extend(errors.iter().map(|d| describe(&world, d)));
            (STATUS_COMPILE_ERROR, finish(Vec::new(), &diagnostics))
        }
    };
    comemo::evict(10);
    Ok(result)
}

/// The creation date written into the PDF, as the CLI computes it: UTC for a fixed timestamp, local time otherwise.
fn timestamp(creation: Option<i64>) -> Option<Timestamp> {
    use chrono::{Datelike, Timelike};
    fn convert<Tz: chrono::TimeZone>(value: &chrono::DateTime<Tz>) -> Option<Datetime> {
        Datetime::from_ymd_hms(
            value.year(),
            value.month().try_into().ok()?,
            value.day().try_into().ok()?,
            value.hour().try_into().ok()?,
            value.minute().try_into().ok()?,
            value.second().try_into().ok()?,
        )
    }

    match creation {
        Some(seconds) => chrono::DateTime::from_timestamp(seconds, 0).and_then(|utc| convert(&utc)).map(Timestamp::new_utc),
        None => {
            let local = chrono::Local::now();
            convert(&local).and_then(|datetime| Timestamp::new_local(datetime, local.offset().local_minus_utc() / 60))
        }
    }
}

fn parse_standards(list: &str) -> Result<PdfStandards, String> {
    let mut standards = Vec::new();
    for name in list.split(',').map(str::trim).filter(|name| !name.is_empty()) {
        let standard: PdfStandard = serde_json::from_value(serde_json::Value::String(name.to_string()))
            .map_err(|_| format!("unknown PDF standard '{name}'"))?;
        standards.push(standard);
    }

    PdfStandards::new(&standards).map_err(|error| error.message().to_string())
}

fn describe(world: &RequestWorld, diagnostic: &SourceDiagnostic) -> Diagnostic {
    let (file, line, column) = locate(world, diagnostic.span);
    Diagnostic {
        severity: match diagnostic.severity {
            Severity::Error => "error",
            Severity::Warning => "warning",
        },
        message: diagnostic.message.to_string(),
        file,
        line,
        column,
        hints: diagnostic.hints.iter().map(|hint| hint.v.to_string()).collect(),
        trace: diagnostic
            .trace
            .iter()
            .map(|point| {
                let (file, line, _) = locate(world, point.span.into());
                match (file, line) {
                    (Some(file), Some(line)) => format!("{} ({file}:{line})", point.v),
                    _ => point.v.to_string(),
                }
            })
            .collect(),
    }
}

/// File name and one-based line and column of a span, when it points into a file.
fn locate(world: &RequestWorld, span: typst::syntax::DiagSpan) -> (Option<String>, Option<usize>, Option<usize>) {
    let Some(id) = span.id() else { return (None, None, None) };
    let name = match id.root() {
        VirtualRoot::Project => id.vpath().get_with_slash().to_string(),
        VirtualRoot::Package(spec) => format!("{spec}{}", id.vpath().get_with_slash()),
    };

    let position = world
        .range(span)
        .zip(world.source(id).ok())
        .and_then(|(range, source)| source.lines().byte_to_line_column(range.start));
    match position {
        Some((line, column)) => (Some(name), Some(line + 1), Some(column + 1)),
        None => (Some(name), None, None),
    }
}

fn finish(pdf: Vec<u8>, diagnostics: &[Diagnostic]) -> CompileResult {
    CompileResult { pdf, diagnostics: serde_json::to_string(diagnostics).unwrap_or_else(|_| "[]".to_string()) }
}

fn failure(message: impl Into<String>) -> CompileResult {
    let diagnostic = Diagnostic {
        severity: "error",
        message: message.into(),
        file: None,
        line: None,
        column: None,
        hints: Vec::new(),
        trace: Vec::new(),
    };
    finish(Vec::new(), &[diagnostic])
}

/// # Safety
/// `ptr` must be valid for `len` elements, or `len` must be zero.
unsafe fn slice_of<'a, T>(ptr: *const T, len: usize) -> &'a [T] {
    if len == 0 || ptr.is_null() { &[] } else { unsafe { std::slice::from_raw_parts(ptr, len) } }
}

fn panic_message(payload: Box<dyn std::any::Any + Send>) -> String {
    payload
        .downcast_ref::<&str>()
        .map(|s| s.to_string())
        .or_else(|| payload.downcast_ref::<String>().cloned())
        .unwrap_or_else(|| "unknown panic".to_string())
}

#[unsafe(no_mangle)]
pub extern "C" fn elarion_typst_abi_version() -> u32 {
    ABI_VERSION
}

/// The Typst version this library embeds, as a static UTF-8 string.
#[unsafe(no_mangle)]
pub extern "C" fn elarion_typst_version() -> ElarionBytes {
    static VERSION: &str = "0.15.1";
    ElarionBytes::of(VERSION.as_bytes())
}

/// Creates an engine. On failure returns a non-zero status and an error result in `out_error` (if non-null).
///
/// # Safety
/// `options` must point to a valid struct whose views stay valid for the call; `out_engine` must be writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn elarion_typst_engine_create(
    options: *const ElarionTypstEngineOptions,
    out_engine: *mut *mut Engine,
    out_error: *mut *mut CompileResult,
) -> i32 {
    if options.is_null() || out_engine.is_null() {
        return STATUS_INVALID_ARGUMENT;
    }

    let outcome = catch_unwind(AssertUnwindSafe(|| Engine::new(unsafe { &*options })));
    let error = match outcome {
        Ok(Ok(engine)) => {
            unsafe { *out_engine = Box::into_raw(Box::new(engine)) };
            return STATUS_OK;
        }
        Ok(Err(message)) => (STATUS_INVALID_ARGUMENT, failure(message)),
        Err(payload) => (STATUS_PANIC, failure(panic_message(payload))),
    };

    if !out_error.is_null() {
        unsafe { *out_error = Box::into_raw(Box::new(error.1)) };
    }
    error.0
}

/// # Safety
/// `engine` must come from [`elarion_typst_engine_create`] and not be used afterwards.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn elarion_typst_engine_destroy(engine: *mut Engine) {
    if !engine.is_null() {
        drop(unsafe { Box::from_raw(engine) });
    }
}

/// Compiles one document to PDF. Always stores a result in `out_result` (unless it is null): the PDF on
/// `STATUS_OK`, diagnostics in every case. Safe to call concurrently on one engine.
///
/// # Safety
/// `engine` must be a live engine; `options` and every view it references must stay valid for the call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn elarion_typst_compile_pdf(
    engine: *const Engine,
    options: *const ElarionTypstCompileOptions,
    out_result: *mut *mut CompileResult,
) -> i32 {
    if engine.is_null() || options.is_null() || out_result.is_null() {
        return STATUS_INVALID_ARGUMENT;
    }

    let outcome = catch_unwind(AssertUnwindSafe(|| compile(unsafe { &*engine }, unsafe { &*options })));
    let (status, result) = match outcome {
        Ok(Ok(result)) => result,
        Ok(Err(message)) => (STATUS_INVALID_ARGUMENT, failure(message)),
        Err(payload) => (STATUS_PANIC, failure(format!("Typst panicked: {}", panic_message(payload)))),
    };
    unsafe { *out_result = Box::into_raw(Box::new(result)) };
    status
}

/// The PDF bytes of a result; empty unless the compilation succeeded. Valid until the result is destroyed.
///
/// # Safety
/// `result` must be a live result.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn elarion_typst_result_pdf(result: *const CompileResult) -> ElarionBytes {
    if result.is_null() { ElarionBytes::EMPTY } else { ElarionBytes::of(unsafe { &(*result).pdf }) }
}

/// The diagnostics of a result as a UTF-8 JSON array. Valid until the result is destroyed.
///
/// # Safety
/// `result` must be a live result.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn elarion_typst_result_diagnostics(result: *const CompileResult) -> ElarionBytes {
    if result.is_null() { ElarionBytes::EMPTY } else { ElarionBytes::of(unsafe { (*result).diagnostics.as_bytes() }) }
}

/// # Safety
/// `result` must come from this library and not be used afterwards.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn elarion_typst_result_destroy(result: *mut CompileResult) {
    if !result.is_null() {
        drop(unsafe { Box::from_raw(result) });
    }
}
