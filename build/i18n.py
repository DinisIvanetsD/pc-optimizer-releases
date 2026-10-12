"""
Ferramenta de tradução do PC Optimizer.

  python build/i18n.py wrap      põe Loc.T(...) à volta dos textos em português do código C#
  python build/i18n.py extract   lista as chaves (como o Loc as constrói) -> build/i18n-keys.json
  python build/i18n.py missing   chaves sem tradução em src/PCOptimizer.Core/Util/en.json

Os textos usados para ler a saída do Windows (Regex, Contains, Split, ...), os padrões de
switch/case, as constantes e os scripts (strings \"\"\"...\"\"\") nunca são tocados.
"""
import html
import json
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = [os.path.join(ROOT, 'src', 'PCOptimizer.App'), os.path.join(ROOT, 'src', 'PCOptimizer.Core')]
EN_JSON = os.path.join(ROOT, 'src', 'PCOptimizer.Core', 'Util', 'en.json')
SKIP_FILES = {'Loc.cs'}

# palavras soltas (não ".com" de um endereço nem partes de caminhos)
PT = re.compile(r"[ãõáéíóúâêôçàÃÕÁÉÍÓÚÂÊÔÇÀ]|(?<![\w./])(não|nao|de|para|com|que|uma?|os|as|do|da|dos|das|no|na|em|se|ou|mais|tens|está|estás|ficheiros?|disco|rede|jogo|instalar|apagar|sem|ainda|já|nenhum|nenhuma|aqui|abrir|abre|ver|fechar|reiniciar|ligado|desligado|erro|aviso|sim|perda|testes?|ignora)(?![\w./])", re.I)

# contextos em que um texto NÃO é para traduzir (compara com o Windows, chaves, padrões, ...)
SKIP_LINE = re.compile(r"Regex|IsMatch|\.Matches?\(|\.Split\(|\.Contains\(|StartsWith\(|EndsWith\(|IndexOf\(|\.Replace\(|\bIs\(|"
                       r"PowerShellAsync|Shell\.RunAsync|Registry|GetValue\(|SetValue\(|OpenSubKey|nameof|\[DllImport|"
                       r"Data\[|\bconst\b|GetProperty\(|TryGetProperty|Environment\.|App\.Log|Debug\.|Console\.")
BEFORE_SKIP = re.compile(r"(\bcase\s*|\bis\s*|\bor\s*|\band\s*|\bnot\s*|==\s*|!=\s*|\[\s*|Loc\.T\(\s*)$")
AFTER_SKIP = re.compile(r"^\s*=>")  # padrão de switch: "texto" => ...


# ------------------------------------------------------------------ lexer de C#

class Lit:
    def __init__(self, start, end, kind, text, holes):
        self.start, self.end, self.kind, self.text, self.holes = start, end, kind, text, holes
        # kind: 'reg' "..", 'verb' @"..", 'interp' $"..", 'interp_verb' $@"..", 'raw' """..."""


def lex(src):
    """Devolve as strings literais de topo (não as que estão dentro de { } de $"...")."""
    out, i, n = [], 0, len(src)
    while i < n:
        c = src[i]
        if src.startswith('//', i):
            i = src.find('\n', i)
            i = n if i < 0 else i
            continue
        if src.startswith('/*', i):
            j = src.find('*/', i + 2)
            i = n if j < 0 else j + 2
            continue
        if c == "'":
            j = i + 1
            while j < n and src[j] != "'":
                j += 2 if src[j] == '\\' else 1
            i = j + 1
            continue
        m = re.match(r'(\$*)(@?)(\$*)("""+|")', src[i:i + 8])
        if m and (m.group(4) or '') and (c in '$@"'):
            lit, i = read_string(src, i)
            out.append(lit)
            continue
        i += 1
    return out


def read_string(src, i):
    start = i
    m = re.match(r'(\$*)(@?)(\$*)("""+|")', src[i:i + 12])
    dollars = len(m.group(1)) + len(m.group(3))
    verbatim = m.group(2) == '@'
    quotes = m.group(4)
    i += len(m.group(0))
    if len(quotes) >= 3:  # raw string: não se traduz
        end = src.find(quotes, i)
        return Lit(start, end + len(quotes), 'raw', src[i:end], []), end + len(quotes)
    interp = dollars > 0
    text, holes, n = [], [], len(src)
    while i < n:
        c = src[i]
        if c == '"':
            if verbatim and i + 1 < n and src[i + 1] == '"':
                text.append('"'); i += 2; continue
            i += 1
            break
        if c == '\\' and not verbatim:
            text.append(src[i:i + 2]); i += 2; continue
        if interp and c == '{':
            if i + 1 < n and src[i + 1] == '{':
                text.append('{{'); i += 2; continue
            # buraco: lê a expressão até ao } de topo
            j, depth = i + 1, 0
            while j < n:
                ch = src[j]
                if ch in '([{': depth += 1
                elif ch in ')]': depth -= 1
                elif ch == '}':
                    if depth == 0: break
                    depth -= 1
                elif ch == '"' or (ch in '$@' and j + 1 < n and src[j + 1] in '"$@'):
                    _, j = read_string(src, j); continue
                elif ch == "'":
                    k = j + 1
                    while k < n and src[k] != "'":
                        k += 2 if src[k] == '\\' else 1
                    j = k
                j += 1
            holes.append((len(''.join(text)), i + 1, j))
            text.append('\x00')
            i = j + 1
            continue
        if interp and c == '}' and i + 1 < n and src[i + 1] == '}':
            text.append('}}'); i += 2; continue
        text.append(c); i += 1
    kind = ('interp_verb' if verbatim else 'interp') if interp else ('verb' if verbatim else 'reg')
    return Lit(start, i, kind, ''.join(text), holes), i


# ------------------------------------------------------------------ chaves (iguais às do Loc em runtime)

ESC = {'n': '\n', 't': '\t', 'r': '\r', '"': '"', '\\': '\\', "'": "'", '0': '\0'}


def unescape(s, verbatim):
    if verbatim:
        return s
    out, i = [], 0
    while i < len(s):
        if s[i] == '\\' and i + 1 < len(s):
            ch = s[i + 1]
            if ch == 'u':
                out.append(chr(int(s[i + 2:i + 6], 16))); i += 6; continue
            out.append(ESC.get(ch, ch)); i += 2; continue
        out.append(s[i]); i += 1
    return ''.join(out)


def hole_spec(expr):
    """expr[,alinhamento][:formato] -> sufixo para a chave ("", ",6", ":N0", ",6:N1")."""
    depth, align, fmt = 0, None, None
    skip_to = [-1]
    for k, ch in enumerate(expr):
        if k <= skip_to[0]:
            continue
        if ch in '([{': depth += 1
        elif ch in ')]}': depth -= 1
        elif ch == '"':
            j = k + 1
            while j < len(expr) and expr[j] != '"':
                j += 2 if expr[j] == chr(92) else 1
            skip_to[0] = j
        elif depth == 0 and ch == ',' and align is None and fmt is None: align = k
        elif depth == 0 and ch == ':' and fmt is None and not expr[k - 1:k] == ':' and not expr[k + 1:k + 2] == ':':
            fmt = k
            break
    suffix = ''
    if align is not None:
        suffix += ',' + expr[align + 1:fmt if fmt is not None else None].strip()
    if fmt is not None:
        suffix += ':' + expr[fmt + 1:]
    return suffix


def key_of(src, lit):
    verbatim = lit.kind in ('verb', 'interp_verb')
    if lit.kind in ('reg', 'verb'):
        return unescape(lit.text, verbatim)
    parts = lit.text.split('\x00')
    key = []
    for idx, part in enumerate(parts):
        # no texto do $"..." as chavetas literais já vêm como {{ }}: o Loc também as duplica
        key.append(unescape(part, verbatim))
        if idx < len(lit.holes):
            _, a, b = lit.holes[idx]
            # o compilador tira os escapes também do formato ({t:hh\\:mm} -> "hh\:mm")
            key.append('{' + str(idx) + unescape(hole_spec(src[a:b]), verbatim) + '}')
    return ''.join(key)


def visible_text(lit):
    return lit.text.replace('\x00', ' ')


# ------------------------------------------------------------------ wrap

def should_wrap(src, lit):
    if lit.kind == 'raw' or not PT.search(visible_text(lit)):
        return False
    line_start = src.rfind('\n', 0, lit.start) + 1
    line_end = src.find('\n', lit.end)
    line = src[line_start:line_end if line_end >= 0 else None]
    before = src[line_start:lit.start]
    after = src[lit.end:line_end if line_end >= 0 else None]
    if line.lstrip().startswith('[') or line.lstrip().startswith('//'):
        return False
    if SKIP_LINE.search(line):
        return False
    if BEFORE_SKIP.search(before) or AFTER_SKIP.match(after):
        return False
    if re.search(r'\b(string|var)\s+\w+\s*=\s*$', before) and re.search(r'\(\s*$|,\s*$', before.split('=')[0]):
        return False  # parâmetro com valor por defeito
    return True


def candidates(src, lits, offset=0, inner=False):
    """Literais a traduzir, incluindo os que estão dentro dos { } de um $"..." (ex.: {(ok ? "sim" : "não")})."""
    out = []
    for lit in lits:
        lit = Lit(lit.start + offset, lit.end + offset, lit.kind, lit.text, [(p, a + offset, b + offset) for p, a, b in lit.holes])
        if (PT.search(visible_text(lit)) and lit.kind != 'raw') if inner else should_wrap(src, lit):
            out.append(lit)
        for _, a, b in lit.holes:
            out.extend(candidates(src, lex(src[a:b]), a, inner=True))
    return out


def wrap_file(path):
    src = open(path, encoding='utf-8-sig').read()
    lits = [l for l in candidates(src, lex(src)) if src[max(0, l.start - 6):l.start] != 'Loc.T(']
    # inserções pontuais do fim para o início (os literais de dentro ficam dentro dos de fora)
    edits = sorted([(l.end, ')') for l in lits] + [(l.start, 'Loc.T(') for l in lits], key=lambda e: e[0], reverse=True)
    for pos, text in edits:
        src = src[:pos] + text + src[pos:]
    if lits and 'using PCOptimizer.Core.Util;' not in src and 'namespace PCOptimizer.Core.Util' not in src:
        m = re.search(r'^(using [^\n]+\n)+', src, re.M)
        insert = m.end() if m else 0
        src = src[:insert] + 'using PCOptimizer.Core.Util;\n' + src[insert:]
    if lits:
        with open(path, 'w', encoding='utf-8-sig', newline='') as f:
            f.write(src)
    return len(lits)


# ------------------------------------------------------------------ extract

def cs_files():
    for base in SRC:
        for d, _, files in os.walk(base):
            if os.sep + 'bin' in d or os.sep + 'obj' in d:
                continue
            for f in files:
                if f.endswith('.cs') and f not in SKIP_FILES:
                    yield os.path.join(d, f)


def xaml_files():
    for d, _, files in os.walk(SRC[0]):
        if os.sep + 'bin' in d or os.sep + 'obj' in d:
            continue
        for f in files:
            if f.endswith('.xaml'):
                yield os.path.join(d, f)


def all_lits(src, lits, offset=0):
    out = []
    for lit in lits:
        lit = Lit(lit.start + offset, lit.end + offset, lit.kind, lit.text, [(p, a + offset, b + offset) for p, a, b in lit.holes])
        out.append(lit)
        for _, a, b in lit.holes:
            out.extend(all_lits(src, lex(src[a:b]), a))
    return out


def extract():
    keys = {}
    for path in cs_files():
        src = open(path, encoding='utf-8-sig').read()
        for lit in all_lits(src, lex(src)):
            if src[max(0, lit.start - 6):lit.start] == 'Loc.T(':
                keys.setdefault(key_of(src, lit), os.path.relpath(path, ROOT))
    for path in xaml_files():
        src = open(path, encoding='utf-8-sig').read()
        for m in re.finditer(r'\b(Text|Content|Header|Tag|ToolTip|Title)="([^"{][^"]*)"', src):
            v = html.unescape(m.group(2))
            # no XAML tudo o que é texto é interface (só fica de fora o que não tem letras ou é técnico)
            if m.group(1) == 'Tag' and ' ' not in v:
                continue  # Tag sem espaços = identificador usado no código, não texto
            if re.search(r"[A-Za-zÀ-ú]{2,}", v) and not re.match(r"^(PC Optimizer|PC Optimizer overlay|CPU|RAM|DNS|SSD|HDD|XMP|MENU|Monitor|Drivers|Ping|Jitter|App|English|Português|10 min|Ctrl \+ .*|PCOptimizer.*\.zip)$", v):
                keys.setdefault(v, os.path.relpath(path, ROOT))
    return keys


def main():
    sys.stdout.reconfigure(encoding='utf-8')
    cmd = sys.argv[1] if len(sys.argv) > 1 else 'extract'
    if cmd == 'wrap':
        total = sum(wrap_file(p) for p in cs_files())
        print(f'{total} textos envolvidos em Loc.T')
    elif cmd == 'extract':
        keys = extract()
        with open(os.path.join(ROOT, 'build', 'i18n-keys.json'), 'w', encoding='utf-8') as f:
            json.dump(keys, f, ensure_ascii=False, indent=1)
        print(f'{len(keys)} chaves')
    elif cmd == 'merge':
        # build/i18n-list.txt = chaves (uma por linha, JSON); build/i18n/en-NNNN.txt = traduções a partir da linha NNNN
        keys = [json.loads(l) for l in open(os.path.join(ROOT, 'build', 'i18n-list.txt'), encoding='utf-8').read().splitlines() if l.strip()]
        en = json.load(open(EN_JSON, encoding='utf-8')) if os.path.exists(EN_JSON) else {}
        folder = os.path.join(ROOT, 'build', 'i18n')
        for name in sorted(os.listdir(folder)):
            m = re.match(r'en-(\d+)\.txt$', name)
            if not m:
                continue
            first = int(m.group(1)) - 1
            lines = [l for l in open(os.path.join(folder, name), encoding='utf-8').read().splitlines() if l.strip()]
            for i, line in enumerate(lines):
                en[keys[first + i]] = json.loads(line)
            print(f'{name}: {len(lines)} (até à chave {first + len(lines)}: {keys[first + len(lines) - 1][:50]!r})')
        with open(EN_JSON, 'w', encoding='utf-8') as f:
            json.dump(en, f, ensure_ascii=False, indent=1, sort_keys=True)
        print(f'{len(en)} traduções em en.json')
    elif cmd == 'audit':
        # textos com cara de frase que ficaram por envolver em Loc.T (para rever à mão)
        sentence = re.compile(r"[a-zà-ú]{3,}\s+[a-zà-ú]{2,}", re.I)
        technical = re.compile(r"\$\w|-\w+Action|\\\\|^[\w.]+$|Get-|Set-|New-|Remove-")
        for path in cs_files():
            src = open(path, encoding='utf-8-sig').read()
            for lit in all_lits(src, lex(src)):
                text = visible_text(lit)
                if lit.kind == 'raw' or src[max(0, lit.start - 6):lit.start] == 'Loc.T(':
                    continue
                if not sentence.search(text) or technical.search(text):
                    continue
                line = src.count('\n', 0, lit.start) + 1
                print(f'{os.path.relpath(path, ROOT)}:{line}: {src[lit.start:lit.end][:110]}')
    elif cmd == 'missing':
        keys = extract()
        en = json.load(open(EN_JSON, encoding='utf-8'))
        missing = {k: v for k, v in keys.items() if k not in en}
        with open(os.path.join(ROOT, 'build', 'i18n-missing.json'), 'w', encoding='utf-8') as f:
            json.dump(missing, f, ensure_ascii=False, indent=1)
        print(f'{len(missing)} sem tradução de {len(keys)}')


if __name__ == '__main__':
    main()
