/**
 * A `dotnet build` transcript with real Elarion diagnostics (ELMOD002 is a
 * warning, ELAUTH001 an error — severities and message texts match the
 * generator source). Used on both marketing pages.
 */
export function BuildOutput() {
  return (
    <div className="code whitespace-pre leading-[1.75]">
      <span className="text-[#5ad6ea]">$</span> dotnet build{'\n'}
      <span className="text-[#64749b]">  Billing.Application → bin/Release/net10.0/Billing.Application.dll{'\n\n'}</span>
      <span className="text-[#64749b]">Modules/Sales/CreateOrder.cs(12,34): </span>
      <span className="text-[#f4c96b]">warning ELMOD002</span>
      <span className="text-[#8398bd]">:</span>
      {'\n'}
      <span className="text-[#b9c6e4]">  Type &apos;Invoice&apos; belongs to module &apos;Billing&apos;; module &apos;Sales&apos; must not{'\n'}</span>
      <span className="text-[#b9c6e4]">  depend on another module&apos;s internals — reach it through a{'\n'}</span>
      <span className="text-[#b9c6e4]">  [ModuleContract], or move the shared type out of the module{'\n\n'}</span>
      <span className="text-[#64749b]">Modules/Billing/ExportInvoices.cs(8,14): </span>
      <span className="text-[#ff7b8a]">error ELAUTH001</span>
      <span className="text-[#8398bd]">:</span>
      {'\n'}
      <span className="text-[#b9c6e4]">  Handler &apos;ExportInvoices&apos; declares an authorization requirement but its{'\n'}</span>
      <span className="text-[#b9c6e4]">  response type &apos;string&apos; does not implement IResultFailureFactory&lt;T&gt;, so{'\n'}</span>
      <span className="text-[#b9c6e4]">  the authorization check cannot short-circuit; return Result&lt;T&gt; or Result{'\n\n'}</span>
      <span className="text-[#ff7b8a]">Build FAILED.</span>
      <span className="text-[#64749b]">  1 Warning(s)  1 Error(s){'\n'}</span>
      <span className="text-[#5ad6ea]">$</span> <span className="caret" />
    </div>
  );
}
