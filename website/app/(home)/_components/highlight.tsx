import type { ReactNode } from 'react';

/**
 * Minimal deterministic tokenizer for the landing page's fixed code samples.
 * Server-rendered spans over the `.code` palette — no client JS, no
 * highlighter dependency. It only needs to be correct for the snippets on
 * this page (every sample is eyeballed), not for arbitrary code.
 */

type Lang = 'cs' | 'ts';

const KEYWORDS: Record<Lang, ReadonlySet<string>> = {
  cs: new Set([
    'public', 'private', 'internal', 'static', 'sealed', 'partial', 'class', 'record',
    'interface', 'async', 'await', 'return', 'is', 'not', 'null', 'new', 'var', 'this',
    'using', 'namespace', 'void', 'string', 'bool', 'int', 'get', 'set', 'init',
    'required', 'readonly', 'true', 'false', 'out', 'ref', 'in', 'where',
  ]),
  ts: new Set([
    'import', 'from', 'export', 'const', 'let', 'await', 'async', 'function', 'return',
    'type', 'as', 'new', 'true', 'false', 'null', 'undefined', 'satisfies',
  ]),
};

type Token = { text: string; cls?: string };

function isIdentStart(ch: string): boolean {
  return /[A-Za-z_$]/.test(ch);
}
function isIdentChar(ch: string): boolean {
  return /[A-Za-z0-9_$]/.test(ch);
}

function tokenizeLine(line: string, lang: Lang): Token[] {
  const tokens: Token[] = [];
  const keywords = KEYWORDS[lang];
  // C# attribute contexts are tracked by [] depth so `[AsParameters] Foo x`
  // colors only the attribute name. Attributes never span lines in our samples.
  let bracketDepth = 0;
  let i = 0;

  const push = (text: string, cls?: string) => {
    if (text.length === 0) return;
    const last = tokens[tokens.length - 1];
    if (last && last.cls === cls) last.text += text;
    else tokens.push({ text, cls });
  };

  while (i < line.length) {
    const ch = line[i];

    // line comment
    if (ch === '/' && line[i + 1] === '/') {
      push(line.slice(i), 'c');
      break;
    }

    // strings: "...", $"...", '...'
    if (ch === '"' || ch === "'" || (ch === '$' && line[i + 1] === '"')) {
      const start = i;
      if (ch === '$') i += 1;
      const quote = line[i];
      i += 1;
      while (i < line.length && line[i] !== quote) {
        if (line[i] === '\\') i += 1;
        i += 1;
      }
      i = Math.min(i + 1, line.length);
      push(line.slice(start, i), 's');
      continue;
    }

    // numbers
    if (/[0-9]/.test(ch)) {
      const start = i;
      while (i < line.length && /[0-9._]/.test(line[i])) i += 1;
      push(line.slice(start, i), 'num');
      continue;
    }

    // identifiers
    if (isIdentStart(ch)) {
      const start = i;
      while (i < line.length && isIdentChar(line[i])) i += 1;
      const word = line.slice(start, i);
      const prev = line.slice(0, start).trimEnd();
      const next = line.slice(i).trimStart();

      if (keywords.has(word)) {
        push(word, 'k');
      } else if (lang === 'cs' && bracketDepth > 0 && /^[A-Z]/.test(word)) {
        push(word, 'a'); // attribute name
      } else if (prev.endsWith('.') && (next.startsWith('(') || next.startsWith('<'))) {
        push(word, 'f'); // method call: db.Clients.Where(...), sp.GetRequiredService<T>()
      } else if (/^[A-Z]/.test(word)) {
        push(word, 't'); // PascalCase → type-ish
      } else if (next.startsWith('(')) {
        push(word, 'f'); // bare call: createRpcApi(...), BuildPipeline(...)
      } else {
        push(word);
      }
      continue;
    }

    if (ch === '[') bracketDepth += 1;
    else if (ch === ']') bracketDepth = Math.max(0, bracketDepth - 1);

    // everything else is punctuation
    push(ch, 'p');
    i += 1;
  }

  return tokens;
}

function renderTokens(tokens: Token[]): ReactNode {
  return tokens.map((tok, i) =>
    tok.cls ? (
      <span key={i} className={tok.cls}>
        {tok.text}
      </span>
    ) : (
      tok.text
    ),
  );
}

/**
 * Highlighted code block with line numbers. `code` is trimmed of the leading
 * newline so samples can be written as readable template literals.
 */
export function Code({ code, lang }: { code: string; lang: Lang }) {
  const lines = code.replace(/^\n/, '').trimEnd().split('\n');
  return (
    <div className="code code-lines">
      {lines.map((line, i) => (
        <span key={i} className="line">
          {line.length > 0 ? renderTokens(tokenizeLine(line, lang)) : ' '}
        </span>
      ))}
    </div>
  );
}
