#!/usr/bin/env python3
"""
Project-wide tree-sitter analysis for the MyAiGen agent engine. Unlike ts_check.py
(single file, run on every write_file), this walks MANY files in one process —
spawning a fresh interpreter per file would dominate the runtime on any real
project, so batching is the whole point here.

Supported languages (grammar packages installed into the embedded interpreter):
  .cs  -> tree-sitter-c-sharp
  .py  -> tree-sitter-python
  .js  -> tree-sitter-javascript (also covers .jsx)
  .ts  -> tree-sitter-typescript (also covers .tsx)
  .go  -> tree-sitter-language-pack (bundled tree-sitter-go)
  .rs  -> tree-sitter-language-pack (bundled tree-sitter-rust)

Subcommands:

  index <project_root>
      Parses every supported code file under project_root and emits, per file,
      the declared symbols (namespace/class/interface/struct/enum/record/
      constructor/method/property/field/function) with kind, name,
      accessibility, and line span. Feeds ProjectIndex — replaces the old
      regex-based symbol extraction with an actual grammar parse, so nested
      braces, generics, lambdas, and symbol names inside comments/strings
      can't produce a false "symbol".

  refs <project_root> <symbol_name>
      Finds every identifier node across all supported files whose text
      matches symbol_name, and classifies each as "declaration" (the
      identifier IS a class/method/field/property/parameter/etc. name at its
      declaration site) or "usage" (anything else — invocation, member access,
      assignment, argument, etc.). Grep-based call-site search matches inside
      string literals and comments too; a syntax-aware walk skips those
      automatically because they're different node kinds entirely.

  methods <project_root> <symbol_name>
      Finds every method/function definition named symbol_name, with its
      signature and precise start/end line span. Backs analyze_method's
      Definitions list, replacing the old per-language regex brace/indent
      matching that used to locate method bodies.

  defs <project_root> <symbol_name>
      Like refs, but only the "declaration" matches — every file:line where
      symbol_name is declared (any kind: class, field, parameter, ...), not
      the call sites. Go-to-definition for arbitrary symbols in one spawn.

  callers <project_root> <symbol_name>
      Every usage site of symbol_name with the enclosing function/method it
      sits inside (climbing ancestors to the nearest callable declaration) —
      "who calls X" grouped by caller, one spawn.

  impls <project_root> <symbol_name>
      For a method/function name: every declaration of it plus the container
      it lives in and that container's base/interface/extends list, so the
      output shows override/implementation relationships at the syntactic
      level (a method declared in class Dog whose base list names Animal, an
      interface method with implementing classes, a Rust trait with impl
      blocks, ...). No type resolution — the agent joins the dots.

  symbols <project_root> [substring]
      The whole project's symbol table: every unique declared name with its
      definition sites (kind, path, line). Optional substring filter. One
      spawn instead of N defs calls.

  search <project_root> <name> [min_params] [max_params]
      Structural search: method/function definitions named <name> whose
      parameter count falls in [min_params, max_params] (defaults: no lower,
      no upper bound). Reports signature, parameter count, and whether the
      body is empty (a stub).

  symbol <project_root> <symbol_name> [min_params] [max_params]
      The combined query behind find_symbol/analyze_method: definitions
      (with signatures), callers (with enclosing callable), and
      override/implementation entries, all from ONE parse pass over the
      project — three spawns' worth of data for the price of one.

  check <file_path>
      Single-file syntax scan for ANY supported language: every ERROR/MISSING
      node with line/column. Backs the write_file-loop syntax feedback for
      .py/.js/.ts (ts_check.py stays the C#-specific checker with its
      placeholder/dead-code extras).

All subcommands always exit 0 and print exactly one line of JSON.

CAVEAT (same for all): this is SYNTACTIC, not semantic. It cannot resolve
overloads, generic instantiation, or which type a member belongs to — a
"usage" of `Run` is any identifier node spelled `Run` in usage position,
regardless of which class it actually belongs to. For a huge win over plain
grep (comments/strings excluded, actual declaration vs. usage distinguished)
without the cost of a real type-checker.

One-time setup (the Windows embeddable Python zip ships without pip):
    Python\\python.exe get-pip.py
    Python\\python.exe -m pip install tree-sitter tree-sitter-c-sharp tree-sitter-python tree-sitter-javascript tree-sitter-typescript tree-sitter-language-pack

Usage:
    python ts_project.py index <project_root>
    python ts_project.py refs <project_root> <symbol_name>
    python ts_project.py methods <project_root> <symbol_name>
    python ts_project.py defs <project_root> <symbol_name>
    python ts_project.py callers <project_root> <symbol_name>
    python ts_project.py impls <project_root> <symbol_name>
    python ts_project.py symbols <project_root> [substring]
    python ts_project.py search <project_root> <name> [min_params] [max_params]
    python ts_project.py symbol <project_root> <name> [min_params] [max_params]
    python ts_project.py check <file_path>
"""
import sys
import os
import json

IGNORED_DIRS = {
    "obj", "bin", "node_modules", ".git", "venv", "__pycache__",
    "target", "build", "dist", ".next",
}

# ext -> (module name, factory function name)
LANGUAGES = {
    ".cs":  ("tree_sitter_c_sharp", "language"),
    ".py":  ("tree_sitter_python", "language"),
    ".js":  ("tree_sitter_javascript", "language"),
    ".jsx": ("tree_sitter_javascript", "language"),
    ".ts":  ("tree_sitter_typescript", "language_typescript"),
    ".tsx": ("tree_sitter_typescript", "language_tsx"),
}

# ext -> language-pack grammar name (get_parser returns a ready Parser).
PACK_LANGUAGES = {
    ".go": "go",
    ".rs": "rust",
}

# ext -> vendored grammar metadata file (node-types.json, pinned to the exact
# grammar versions installed in Python\Lib\site-packages). The runtime
# Language API exposes node-kind and field-name ENUMERATIONS, but not the
# per-kind field maps — those live in node-types.json. We derive every
# structural fact (which kinds have a `name` field, what a `body` field may
# hold, which kinds count as "has parameters") from the grammar's own metadata
# instead of hand-curating node-type lists that silently rot on grammar
# upgrades.
GRAMMAR_FILES = {
    ".cs":  "c-sharp.json",
    ".py":  "python.json",
    ".js":  "javascript.json",
    ".jsx": "javascript.json",
    ".ts":  "typescript.json",
    ".tsx": "typescript.json",
    ".go":  "go.json",
    ".rs":  "rust.json",
}

_parsers = {}
_profiles = {}


def get_parser(ext):
    if ext in _parsers:
        return _parsers[ext]
    if ext in PACK_LANGUAGES:
        from tree_sitter_language_pack import get_parser as pack_get_parser
        parser = pack_get_parser(PACK_LANGUAGES[ext])
    else:
        mod_name, fn_name = LANGUAGES[ext]
        mod = __import__(mod_name)
        from tree_sitter import Language, Parser
        parser = Parser(Language(getattr(mod, fn_name)()))
    _parsers[ext] = parser
    return parser


class GrammarProfile:
    """Grammar metadata loaded from the vendored node-types.json, cross-
    validated against the runtime Language API once per process. This is the
    single source of truth for node-kind vocabulary; the curated semantic
    tables (kind -> symbol kind) are validated against it at startup."""

    def __init__(self, ext):
        self.ext = ext
        self.kind_fields = {}        # kind -> set(field names)
        self.field_kinds = {}        # field name -> set(kinds the field accepts)
        self.kind_children = {}      # kind -> True when it has children entries
        self.name_field_kinds = set()
        self.leaf_name_kinds = set()
        self._load()

    def _load(self):
        path = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                            "grammar", GRAMMAR_FILES[self.ext])
        with open(path, encoding="utf-8") as f:
            entries = json.load(f)
        for e in entries:
            kind = e.get("type")
            if not kind or not e.get("named") or e.get("subtypes"):
                continue
            fields = e.get("fields", {})
            self.kind_fields[kind] = set(fields)
            self.kind_children[kind] = bool(e.get("children"))
            for fname, finfo in fields.items():
                accepted = self.field_kinds.setdefault(fname, set())
                for t in finfo.get("types", []):
                    if t.get("named"):
                        accepted.add(t["type"])
            if "name" in fields:
                self.name_field_kinds.add(kind)
        # Leaf name kinds: kinds a `name` field can hold that have no children
        # of their own — real identifier leaves (identifier, property_identifier,
        # type_identifier) plus literal member names (`{ "foo"() {} }`, `{ 1: 2 }`)
        # — but NOT compound names (qualified_name, generic_name, patterns).
        self.leaf_name_kinds = {
            k for k in self.field_kinds.get("name", ())
            if not self.kind_children.get(k)
        }

    def has_field(self, kind, field):
        return field in self.kind_fields.get(kind, ())

    def name_identifier_kinds(self):
        """Identifier-like kinds that a `name` field accepts and that have no
        children — the node kinds the refs walk treats as symbol references."""
        return {k for k in self.leaf_name_kinds if "identifier" in k}

    def method_like_kinds(self):
        """Kinds that declare a NAMED, parameterized symbol (methods, functions,
        constructors, signatures, delegates, destructors) — everything the
        grammar says has both a name field and a parameter list."""
        return {k for k, fields in self.kind_fields.items()
                if "name" in fields and ("parameters" in fields
                                         or "formal_parameters" in fields)}

    def type_name_field_kinds(self):
        """Kinds whose `name` field accepts a type_identifier — the declaration
        sites for TypeScript type symbols."""
        return {k for k in self.name_field_kinds
                if "type_identifier" in self.field_kinds.get("name", ())}

    def terminators(self, kind):
        """Kinds that end a declaration's signature: whatever the grammar says
        the `body`/`accessors` fields may hold, plus `;` (signatures)."""
        terms = {";"}
        for fname in ("body", "accessors"):
            terms |= self.field_kinds.get(fname, set())
        return terms

    def validate(self, parser):
        """Cross-check the vendored JSON against the runtime grammar; log drift
        to stderr (never stdout — the C# side parses stdout as JSON)."""
        lang = parser.language
        runtime = set()
        for i in range(lang.node_kind_count):
            if lang.node_kind_is_named(i):
                k = lang.node_kind_for_id(i)
                if k is not None:
                    runtime.add(k)
        known = set(self.kind_fields)
        drift = runtime - known
        if drift:
            print(f"[ts_project] {self.ext}: grammar kinds absent from node-types.json: "
                  f"{sorted(drift)}", file=sys.stderr)


def get_profile(ext):
    if ext not in _profiles:
        _profiles[ext] = GrammarProfile(ext)
    return _profiles[ext]


def emit(payload):
    print(json.dumps(payload))
    sys.exit(0)


def fail(reason):
    emit({"ok": None, "reason": reason})


def iter_code_files(project_root):
    for dirpath, dirnames, filenames in os.walk(project_root):
        dirnames[:] = [d for d in dirnames if d not in IGNORED_DIRS and not d.startswith(".")]
        for fn in filenames:
            ext = os.path.splitext(fn)[1].lower()
            if ext in LANGUAGES or ext in PACK_LANGUAGES:
                yield os.path.join(dirpath, fn), ext


# ---- shared helpers -------------------------------------------------------

NAME_NODES = {"identifier", "property_identifier", "type_identifier"}


def _name_kinds(profile):
    """Leaf name kinds for a language: the grammar's own `name` field value
    kinds, filtered to leaves. Falls back to the classic identifier kinds."""
    if profile is not None and profile.leaf_name_kinds:
        return profile.leaf_name_kinds
    return NAME_NODES


def declared_name(node, profile=None):
    """First name-node child of a declaration — the declared name for the
    declaration node types this script inspects. The grammar's `name` field is
    authoritative: node-types.json says which kinds carry one and what value
    kinds it accepts, so the "return type before name" C# shape needs no
    special case here."""
    name_node = declared_name_node(node, profile)
    if name_node is None:
        return None
    return name_node.text.decode("utf-8", errors="replace")


def declared_name_node(node, profile=None):
    """The name node itself (not its text). Same selection as declared_name,
    but lets callers anchor spans/signatures at the name rather than the whole
    declaration — attribute lists and modifiers precede the name and would
    otherwise shift start lines and pollute signatures."""
    named = node.child_by_field_name("name")
    if named is not None and named.type in _name_kinds(profile):
        return named
    for child in node.children:
        if child.type in _name_kinds(profile):
            return child
    return None


def node_modifiers(node):
    """Set of modifier keywords that are direct children of a declaration.
    C# uses `modifier` nodes; TypeScript uses `accessibility_modifier`
    (public/private/protected); Go/Rust use `visibility_modifier` (pub)."""
    mods = set()
    for child in node.children:
        if child.type in ("modifier", "accessibility_modifier", "visibility_modifier"):
            for gc in child.children:
                mods.add(gc.type)
    return mods


def accessibility_of(mods, default):
    if "public" in mods:
        return "public"
    if "private" in mods:
        return "private"
    if "protected" in mods and "internal" in mods:
        return "protected internal"
    if "protected" in mods:
        return "protected"
    if "internal" in mods:
        return "internal"
    return default


def span(node):
    return (node.start_point[0] + 1, node.end_point[0] + 1)


def decl_span(node, profile=None):
    """(start_line, end_line) for a declaration, anchored at the declared name
    when one is present. Attribute lists and modifiers precede the name node,
    so using the whole node's start point would report the attribute line as
    the declaration line."""
    name = declared_name_node(node, profile)
    if name is not None:
        return (name.start_point[0] + 1, node.end_point[0] + 1)
    return span(node)


def read_source(full_path):
    with open(full_path, "r", encoding="utf-8-sig", errors="replace") as f:
        return f.read()


# ---- index: symbol extraction --------------------------------------------

# C# name-field kinds that are USAGE positions, not declaration sites: the
# grammar assigns a `name` field to named-argument, attribute, member-access
# and qualified-name nodes too, and the identifier there is a reference.
CS_NAME_USAGE_KINDS = {
    "argument", "attribute", "attribute_argument",
    "member_access_expression", "member_binding_expression",
    "qualified_name", "alias_qualified_name",
    "parameter_list", "bracketed_parameter_list", "accessor_declaration",
}


def cs_decl_parents(profile):
    """C# declaration-site parents, derived from the grammar: every kind that
    carries a `name` field, minus the usage-name kinds above, plus
    foreach_statement (its loop variable is the `left` field, not `name`)."""
    kinds = set(profile.name_field_kinds) - CS_NAME_USAGE_KINDS
    kinds.add("foreach_statement")
    return kinds


TYPE_DECL_KINDS = {
    "class_declaration": "class",
    "abstract_class_declaration": "class",
    "interface_declaration": "interface",
    "struct_declaration": "struct",
    "enum_declaration": "enum",
    "record_declaration": "record",
}

MEMBER_ACCESS_MODIFIERS = {"public", "private", "protected", "internal"}

# TypeScript type_identifier declaration parents: every kind whose `name` field
# accepts a type_identifier per the grammar (class/interface/type-alias/type-
# parameter names), minus usage positions (generic instantiations, dotted type
# references), plus enum_declaration — its name field is a plain identifier,
# but an enum name IS a type symbol.
TS_TYPE_USAGE_KINDS = {"generic_type", "nested_type_identifier"}


def ts_type_decl_parents(profile):
    kinds = set(profile.type_name_field_kinds()) - TS_TYPE_USAGE_KINDS
    kinds.add("enum_declaration")
    return kinds


def index_csharp(parser, source, profile):
    tree = parser.parse(bytes(source, "utf-8"))
    symbols = []

    def emit(kind, name, acc, s, e):
        symbols.append({
            "kind": kind,
            "name": name,
            "accessibility": acc,
            "start_line": s,
            "end_line": e,
        })

    def visit(node, container_kind):
        for child in node.children:
            ctype = child.type

            if ctype in TYPE_DECL_KINDS:
                this_kind = TYPE_DECL_KINDS[ctype]
                mods = node_modifiers(child)
                name = declared_name(child, profile)
                if name:
                    s, e = decl_span(child, profile)
                    emit(this_kind, name,
                         accessibility_of(mods, "internal" if container_kind is None else "private"),
                         s, e)
                if this_kind == "record":
                    # Record positional parameters are public properties
                    # (init-only, one per `parameter` child).
                    plist = next((c for c in child.children if c.type == "parameter_list"), None)
                    if plist is not None:
                        for p in plist.children:
                            if p.type != "parameter":
                                continue
                            pname = declared_name(p, profile)
                            if pname:
                                emit("property", pname, "public",
                                     p.start_point[0] + 1, p.end_point[0] + 1)
                visit(child, this_kind)

            elif ctype == "namespace_declaration":
                visit(child, container_kind)

            elif container_kind is not None and ctype in profile.method_like_kinds():
                if ctype not in (
                    "method_declaration", "constructor_declaration", "property_declaration",
                    "destructor_declaration", "event_declaration",
                    "local_function_statement",
                ):
                    # method_like_kinds also covers operator/conversion/indexer/
                    # delegate (name or parameters fields) — those have their
                    # own branches below.
                    pass
                else:
                    mods = node_modifiers(child)
                    kind = {"method_declaration": "method", "constructor_declaration": "constructor",
                            "property_declaration": "property", "destructor_declaration": "destructor",
                            "event_declaration": "event",
                            "local_function_statement": "method"}[ctype]
                    if ctype == "destructor_declaration":
                        # `~Base()` — the name field is the class name; expose it as `~Base`.
                        name = "~" + (declared_name(child, profile) or "")
                    else:
                        name = declared_name(child, profile)
                    default_acc = "private" if container_kind in ("class", "struct", "record") else "public"
                    if name:
                        s, e = decl_span(child, profile)
                        emit(kind, name, accessibility_of(mods, default_acc), s, e)

            elif ctype == "indexer_declaration":
                mods = node_modifiers(child)
                default_acc = "private" if container_kind in ("class", "struct", "record") else "public"
                s, e = span(child)
                emit("indexer", "this", accessibility_of(mods, default_acc), s, e)

            elif ctype in ("operator_declaration", "conversion_operator_declaration"):
                mods = node_modifiers(child)
                # `operator +` / `operator ==`; conversions are `implicit operator int`
                # / `explicit operator long` (the `explicit`/`implicit` keyword is a
                # bare child; the target type is the `type` field).
                op_tok = child.child_by_field_name("operator")
                if op_tok is not None and ctype == "operator_declaration":
                    name = "operator " + op_tok.text.decode("utf-8", errors="replace")
                else:
                    kw = next((c.text.decode("utf-8", errors="replace")
                               for c in child.children
                               if c.type in ("implicit", "explicit")), "")
                    ret = child.child_by_field_name("type")
                    ret_text = ret.text.decode("utf-8", errors="replace") if ret is not None else ""
                    name = f"{kw} operator {ret_text}".strip()
                default_acc = "private" if container_kind in ("class", "struct", "record") else "public"
                s, e = span(child)
                emit("operator", name, accessibility_of(mods, default_acc), s, e)

            elif container_kind == "enum" and ctype == "enum_member_declaration":
                name = declared_name(child, profile)
                if name:
                    s, e = decl_span(child, profile)
                    emit("enum_member", name, "public", s, e)

            elif ctype == "enum_member_declaration_list":
                visit(child, container_kind)

            elif ctype == "delegate_declaration":
                mods = node_modifiers(child)
                name = declared_name(child, profile)
                if name:
                    s, e = decl_span(child, profile)
                    emit("delegate", name,
                         accessibility_of(mods, "internal" if container_kind is None else "private"),
                         s, e)

            elif ctype in ("field_declaration", "event_field_declaration"):
                mods = node_modifiers(child)
                default_acc = "private" if container_kind in ("class", "struct", "record") else "public"
                acc = accessibility_of(mods, default_acc)
                kind = "event" if ctype == "event_field_declaration" else "field"
                var_decl = next((c for c in child.children if c.type == "variable_declaration"), None)
                if var_decl is not None:
                    for vc in var_decl.children:
                        if vc.type == "variable_declarator":
                            name = declared_name(vc, profile)
                            if name:
                                s = declared_name_node(vc, profile).start_point[0] + 1
                                emit(kind, name, acc, s, s)

            elif ctype == "declaration_list":
                visit(child, container_kind)

    visit(tree.root_node, None)
    return symbols


def index_python(parser, source):
    tree = parser.parse(bytes(source, "utf-8"))
    symbols = []

    def visit(node, in_class):
        for child in node.children:
            ctype = child.type

            if ctype == "class_definition":
                name = declared_name(child)
                if name:
                    s, e = decl_span(child)
                    symbols.append({"kind": "class", "name": name, "accessibility": "", "start_line": s, "end_line": e})
                visit(child, True)

            elif ctype == "function_definition":
                name = declared_name(child)
                if name:
                    kind = "method" if in_class else "function"
                    s, e = decl_span(child)
                    symbols.append({"kind": kind, "name": name, "accessibility": "", "start_line": s, "end_line": e})
                # nested defs (closures) are not project API — don't descend.

            elif ctype == "block":
                visit(child, in_class)

            elif ctype == "decorated_definition":
                visit(child, in_class)

    visit(tree.root_node, False)
    return symbols


# JS/TS member declarations inside a class/interface body.
JS_TS_MEMBER_KINDS = {
    "method_definition": "method",
    "abstract_method_signature": "method",
    "method_signature": "method",
    "property_signature": "property",
}

# JS/TS function-valued nodes: module-level `const f = () => {}`, class
# fields `m2 = () => {}` and typed/accessor fields `private m3 = () => {}`
# behave as functions. (TS uses `public_field_definition` once an access
# modifier or explicit type is present.)
FUNCTION_VALUED_NODES = {"variable_declarator", "field_definition", "public_field_definition"}
ARROW_VALUE_TYPES = {"arrow_function", "function_expression", "generator_function"}


def index_js_ts(parser, source, profile):
    tree = parser.parse(bytes(source, "utf-8"))
    symbols = []

    def visit(node, in_class):
        for child in node.children:
            ctype = child.type

            if ctype in TYPE_DECL_KINDS:
                mods = node_modifiers(child)
                name = declared_name(child, profile)
                kind = TYPE_DECL_KINDS[ctype]
                if name:
                    s, e = decl_span(child, profile)
                    symbols.append({
                        "kind": kind,
                        "name": name,
                        "accessibility": accessibility_of(mods, ""),
                        "start_line": s,
                        "end_line": e,
                    })
                visit(child, True)

            elif ctype in ("function_declaration", "generator_function_declaration"):
                name = declared_name(child, profile)
                if name:
                    s, e = decl_span(child, profile)
                    symbols.append({"kind": "function", "name": name, "accessibility": "", "start_line": s, "end_line": e})

            elif in_class and ctype in JS_TS_MEMBER_KINDS:
                mods = node_modifiers(child)
                name = declared_name(child, profile)
                kind = JS_TS_MEMBER_KINDS[ctype]
                if name == "constructor":
                    kind = "constructor"
                if name:
                    s, e = decl_span(child, profile)
                    symbols.append({
                        "kind": kind,
                        "name": name,
                        "accessibility": accessibility_of(mods, ""),
                        "start_line": s,
                        "end_line": e,
                    })

            elif ctype in FUNCTION_VALUED_NODES:
                value = child.child_by_field_name("value")
                if value is not None and value.type in ARROW_VALUE_TYPES:
                    name = declared_name(child, profile)
                    if name:
                        s, e = decl_span(child, profile)
                        symbols.append({"kind": "function", "name": name, "accessibility": "", "start_line": s, "end_line": e})

            elif ctype == "enum_body" and in_class:
                # TS enum members: bare `Red` (property_identifier with the
                # `name` field) or `Green = 5` (enum_assignment).
                for m in child.children:
                    if m.type == "property_identifier" or m.type == "enum_assignment":
                        name = declared_name(m, profile) if m.type == "enum_assignment" else m.text.decode("utf-8", errors="replace")
                        if name:
                            symbols.append({"kind": "enum_member", "name": name, "accessibility": "",
                                            "start_line": m.start_point[0] + 1, "end_line": m.start_point[0] + 1})

            # JS/TS wrapper nodes — descend but preserve the container kind.
            elif ctype in ("class_body", "interface_body", "enum_body",
                           "export_statement", "lexical_declaration",
                           "variable_declaration"):
                visit(child, in_class)

    visit(tree.root_node, False)
    return symbols


# Go type-declaration kinds (type_spec's `type` field decides which).
GO_TYPE_KIND = {
    "struct_type": "struct",
    "interface_type": "interface",
}

GO_CONTAINER_KINDS = {"struct", "interface"}


def go_receiver_name(method_node, profile):
    """Container name for a Go method: the receiver type text
    (`func (c Circle) Area()` -> "Circle")."""
    recv = method_node.child_by_field_name("receiver")
    if recv is None:
        return ""
    for c in recv.children:
        if c.type in ("parameter_declaration", "variadic_parameter_declaration"):
            t = c.child_by_field_name("type")
            if t is not None:
                return t.text.decode("utf-8", errors="replace")
            # unnamed receiver: `func (Circle) Area()` — type is a direct child.
            named = [x for x in c.children if x.is_named]
            if named:
                return named[0].text.decode("utf-8", errors="replace")
    return ""


def index_go(parser, source, profile):
    tree = parser.parse(bytes(source, "utf-8"))
    symbols = []

    def emit(kind, name, s, e):
        symbols.append({"kind": kind, "name": name, "accessibility": "", "start_line": s, "end_line": e})

    def visit(node, container_kind):
        for child in node.children:
            ctype = child.type

            if ctype == "package_clause":
                pkg = next((c for c in child.children if c.type == "package_identifier"), None)
                if pkg is not None:
                    emit("package", pkg.text.decode("utf-8", errors="replace"),
                         pkg.start_point[0] + 1, pkg.start_point[0] + 1)

            elif ctype == "type_declaration":
                for spec in child.children:
                    if spec.type == "type_spec":
                        name = declared_name(spec, profile)
                        if not name:
                            continue
                        tfield = spec.child_by_field_name("type")
                        this_kind = GO_TYPE_KIND.get(tfield.type if tfield is not None else "", "type")
                        s, e = decl_span(spec, profile)
                        emit(this_kind, name, s, e)
                        visit(spec, this_kind)
                    elif spec.type == "type_alias":
                        name = declared_name(spec, profile)
                        if name:
                            s, e = decl_span(spec, profile)
                            emit("type", name, s, e)

            elif ctype == "method_declaration":
                name = declared_name(child, profile)
                if name:
                    s, e = decl_span(child, profile)
                    emit("method", name, s, e)

            elif ctype == "function_declaration":
                name = declared_name(child, profile)
                if name:
                    s, e = decl_span(child, profile)
                    emit("function", name, s, e)

            elif ctype == "const_declaration":
                for spec in child.children:
                    if spec.type == "const_spec":
                        name = declared_name(spec, profile)
                        if name:
                            s, e = decl_span(spec, profile)
                            emit("const", name, s, e)

            elif ctype == "var_declaration":
                for spec in child.children:
                    if spec.type == "var_spec":
                        name = declared_name(spec, profile)
                        if name:
                            s, e = decl_span(spec, profile)
                            emit("var", name, s, e)

            elif container_kind in GO_CONTAINER_KINDS and ctype == "field_declaration":
                name = declared_name(child, profile)
                if name:
                    s, e = decl_span(child, profile)
                    emit("field", name, s, e)

            elif ctype == "field_declaration_list":
                visit(child, container_kind)

            elif ctype in ("struct_type", "interface_type"):
                visit(child, container_kind)

    visit(tree.root_node, None)
    return symbols


RUST_TYPE_KINDS = {
    "struct_item": "struct",
    "enum_item": "enum",
    "trait_item": "trait",
    "type_item": "type",
    "union_item": "union",
    "mod_item": "module",
}


def index_rust(parser, source, profile):
    tree = parser.parse(bytes(source, "utf-8"))
    symbols = []

    def emit(kind, name, s, e, acc=""):
        symbols.append({"kind": kind, "name": name, "accessibility": acc, "start_line": s, "end_line": e})

    def visit(node, in_container):
        for child in node.children:
            ctype = child.type

            if ctype in RUST_TYPE_KINDS:
                name = declared_name(child, profile)
                this_kind = RUST_TYPE_KINDS[ctype]
                mods = node_modifiers(child)
                acc = "pub" if "pub" in mods else ""
                if name:
                    s, e = decl_span(child, profile)
                    emit(this_kind, name, s, e, acc)
                if this_kind in ("struct", "enum", "trait", "union", "module"):
                    visit(child, True)

            elif ctype == "impl_item":
                visit(child, True)

            elif ctype in ("function_item", "function_signature_item"):
                name = declared_name(child, profile)
                mods = node_modifiers(child)
                acc = "pub" if "pub" in mods else ""
                if name:
                    kind = "method" if in_container else "function"
                    s, e = decl_span(child, profile)
                    emit(kind, name, s, e, acc)

            elif ctype == "field_declaration" and in_container:
                name = declared_name(child, profile)
                mods = node_modifiers(child)
                acc = "pub" if "pub" in mods else ""
                if name:
                    s, e = decl_span(child, profile)
                    emit("field", name, s, e, acc)

            elif ctype == "enum_variant" and in_container:
                name = declared_name(child, profile)
                if name:
                    emit("enum_member", name, child.start_point[0] + 1, child.start_point[0] + 1)

            elif ctype == "const_item" and in_container:
                name = declared_name(child, profile)
                if name:
                    emit("const", name, child.start_point[0] + 1, child.start_point[0] + 1)

            elif ctype == "declaration_list":
                visit(child, in_container)

            elif ctype == "field_declaration_list":
                visit(child, in_container)

            elif ctype == "enum_variant_list":
                visit(child, in_container)

    visit(tree.root_node, False)
    return symbols


def cmd_index(project_root):
    files_out = {}
    for full_path, ext in iter_code_files(project_root):
        rel = os.path.relpath(full_path, project_root).replace("\\", "/")
        try:
            parser = get_parser(ext)
        except Exception as e:
            files_out[rel] = {"symbols": [], "error": f"grammar load failed: {e}"}
            continue
        try:
            source = read_source(full_path)
        except OSError as e:
            files_out[rel] = {"symbols": [], "error": f"read failed: {e}"}
            continue
        try:
            profile = get_profile(ext)
            if ext == ".cs":
                symbols = index_csharp(parser, source, profile)
            elif ext == ".py":
                symbols = index_python(parser, source)
            elif ext == ".go":
                symbols = index_go(parser, source, profile)
            elif ext == ".rs":
                symbols = index_rust(parser, source, profile)
            else:
                symbols = index_js_ts(parser, source, profile)
        except Exception as e:
            files_out[rel] = {"symbols": [], "error": f"parse failed: {e}"}
            continue
        files_out[rel] = {"symbols": symbols, "error": None}
    emit({"ok": True, "files": files_out})


# ---- refs: declaration vs usage classification ----------------------------

# Python declaration-site node types.
PY_DECL_PARENTS = {
    "function_definition", "class_definition", "parameters",
    "parameter", "typed_parameter", "default_parameter", "typed_default_parameter",
    "list_splat_pattern", "dictionary_splat_pattern",
    "tuple_pattern", "list_pattern", "pattern_list",
    "aliased_import", "global_statement", "nonlocal_statement",
    "lambda_parameters",
}
# Python parents where ONLY the first child is the declared name
# (e.g. `x = f()` -> x; `for x in xs` -> x; `y := z` -> y).
PY_FIRST_CHILD_DECL = {"assignment", "augmented_assignment", "named_expression", "for_in_clause"}
# Python parents where ONLY the last child is the declared name
# (`with expr as fh` -> fh; `except E as e` -> e).
PY_LAST_CHILD_DECL = {"with_item", "except_clause"}
# Identifiers here are keyword names, not symbol references at all.
PY_SKIP = {"keyword_argument"}


def is_py_decl(node):
    ptype = node.parent.type if node.parent is not None else ""
    if ptype in PY_SKIP:
        return None
    if ptype in PY_DECL_PARENTS:
        return True
    kids = node.parent.children if node.parent is not None else []
    if ptype in PY_FIRST_CHILD_DECL:
        return bool(kids) and kids[0].id == node.id
    if ptype in PY_LAST_CHILD_DECL:
        return bool(kids) and kids[-1].id == node.id
    return False


# JS/TS declaration-site node types. Note `required_parameter` covers TS
# parameters (even untyped ones), while plain JS parameters are bare
# identifiers directly under `formal_parameters`.
JS_TS_DECL_PARENTS = {
    "function_declaration", "generator_function_declaration", "class_declaration",
    "abstract_class_declaration", "interface_declaration", "enum_declaration",
    "enum_member_declaration", "field_definition", "public_field_definition",
    "method_definition", "abstract_method_signature", "method_signature",
    "property_signature", "function_expression", "generator_function",
    "formal_parameters", "required_parameter", "optional_parameter",
    "rest_pattern", "assignment_pattern", "catch_clause",
    "import_specifier", "export_specifier", "enum_body",
}
# Parents where ONLY the first child is the declared name (`const x = ...`).
JS_TS_FIRST_CHILD_DECL = {"variable_declarator"}


def is_js_ts_decl(node):
    ptype = node.parent.type if node.parent is not None else ""
    if ptype in JS_TS_DECL_PARENTS:
        return True
    kids = node.parent.children if node.parent is not None else []
    if ptype in JS_TS_FIRST_CHILD_DECL:
        return bool(kids) and kids[0].id == node.id
    return False


def is_ts_type_decl(node, profile):
    ptype = node.parent.type if node.parent is not None else ""
    return ptype in ts_type_decl_parents(profile)


# Go name-field kinds that are USAGE positions: `pkg.T` as a TYPE — the T is
# the `name` field of qualified_type but refers to an existing symbol.
GO_NAME_USAGE_KINDS = {"qualified_type"}
# Go parents where the declared name sits in the `left` field, not `name`.
GO_LEFT_DECL_PARENTS = {"short_var_declaration", "range_clause"}


def go_decl_parents(profile):
    """Go declaration-site parents: every kind with a `name` field, minus the
    usage positions above."""
    return profile.name_field_kinds - GO_NAME_USAGE_KINDS


# Rust name-field kinds that are USAGE positions: the tail segment of a path
# (scoped_identifier/scoped_type_identifier), the type being constructed in a
# struct_expression, a struct field matched by a field_pattern, and a generic
# binding in a qualified path (type_binding).
RUST_NAME_USAGE_KINDS = {"scoped_identifier", "scoped_type_identifier",
                         "struct_expression", "field_pattern", "type_binding"}
# Rust parents where a plain identifier is a BINDING (let/for/params/patterns)
# rather than a reference. The grammar puts these in the `pattern` field (or
# bare, for untyped params), not the `name` field.
RUST_PATTERN_PARENTS = {
    "let_declaration", "let_condition", "for_expression", "closure_parameters",
    "parameters", "typed_parameter", "self_parameter",
    "tuple_pattern", "parenthesized_pattern", "or_pattern", "slice_pattern",
    "array_pattern", "reference_pattern", "field_pattern", "match_pattern",
}


def rust_decl_parents(profile):
    return profile.name_field_kinds - RUST_NAME_USAGE_KINDS


def _parent_info(node):
    ptype = ""
    fld = None
    if node.parent is not None:
        ptype = node.parent.type
        for i, c in enumerate(node.parent.children):
            if c.id == node.id:
                fld = node.parent.field_name_for_child(i)
                break
    return ptype, fld


def classify_name(node, symbol_name, ext, profile):
    """Classify one name-leaf node as "declaration", "usage", or None (not a
    reference at all — a keyword name, an object-literal key, ...)."""
    ntype = node.type
    if node.text.decode("utf-8", errors="replace") != symbol_name:
        return None

    if ext == ".py":
        if ntype != "identifier":
            return None
        result = is_py_decl(node)
        return None if result is None else ("declaration" if result else "usage")

    if ext == ".cs":
        # C# declaration sites are the `name` field child of a declaration
        # parent — the `type`/`qualifier` field identifiers (parameter types,
        # namespace parts) sit under the SAME parent node, so parent-type alone
        # is not enough. `foreach` names use the `left` field.
        if ntype != "identifier":
            return None
        ptype, fld = _parent_info(node)
        is_name = fld == "name" or (ptype == "foreach_statement" and fld == "left")
        return "declaration" if (ptype in cs_decl_parents(profile) and is_name) else "usage"

    if ext == ".go":
        # Same name-field rule as C#, plus `left`-field declarations
        # (`x := 5`, `for k, v := range m`) and the package clause name.
        ptype, fld = _parent_info(node)
        if ntype == "package_identifier":
            return "declaration"
        is_name = fld == "name" or (ptype in GO_LEFT_DECL_PARENTS and fld == "left")
        return "declaration" if (ptype in go_decl_parents(profile) and is_name) else "usage"

    if ext == ".rs":
        # Two rules: pattern-position identifiers are bindings; everything else
        # follows the name-field rule minus usage positions (path tails etc.).
        ptype, fld = _parent_info(node)
        if ntype == "identifier" and ptype in RUST_PATTERN_PARENTS:
            return "declaration"
        if fld == "name" and ptype in rust_decl_parents(profile):
            return "declaration"
        return "usage"

    # JS/TS
    if ntype == "type_identifier":
        # Type positions (`x: Foo`, `new Foo()`, generics...) are usages unless
        # the node is the declared name of a type declaration.
        return "declaration" if is_ts_type_decl(node, profile) else "usage"
    if ntype == "property_identifier":
        ptype, _ = _parent_info(node)
        if ptype == "pair":
            return None  # object literal key `{foo: ...}` — not a reference
    return "declaration" if is_js_ts_decl(node) else "usage"


def iter_ref_matches(project_root, symbol_name):
    """Yield (rel, ext, node, kind) for every matching name node across the
    project. One parse per file; kind is "declaration" or "usage"."""
    for full_path, ext in iter_code_files(project_root):
        rel = os.path.relpath(full_path, project_root).replace("\\", "/")
        try:
            parser = get_parser(ext)
            source = read_source(full_path)
            tree = parser.parse(bytes(source, "utf-8"))
        except Exception:
            continue

        profile = get_profile(ext)
        name_kinds = profile.name_identifier_kinds() | NAME_NODES

        def walk(node):
            ntype = node.type
            if ntype in name_kinds:
                # Name nodes are leaves — nothing to descend into below a match.
                kind = classify_name(node, symbol_name, ext, profile)
                if kind is not None:
                    yield rel, ext, node, kind
                return
            for child in node.children:
                yield from walk(child)

        yield from walk(tree.root_node)


def _line_context(lines, line_no):
    context = lines[line_no].strip() if 0 <= line_no < len(lines) else ""
    if len(context) > 140:
        context = context[:140] + "..."
    return context


def cmd_refs(project_root, symbol_name):
    matches = []
    MAX_MATCHES = 300
    for rel, ext, node, kind in iter_ref_matches(project_root, symbol_name):
        if len(matches) >= MAX_MATCHES:
            break
        line_no = node.start_point[0]
        matches.append({
            "path": rel,
            "line": line_no + 1,
            "column": node.start_point[1] + 1,
            "kind": kind,
            "context": _line_context(_lines_for(project_root, rel), line_no),
        })
    emit({"ok": True, "matches": matches})


def cmd_defs(project_root, symbol_name):
    """Go-to-definition: every declaration site of symbol_name (any kind)."""
    matches = []
    MAX_MATCHES = 300
    for rel, ext, node, kind in iter_ref_matches(project_root, symbol_name):
        if kind != "declaration":
            continue
        if len(matches) >= MAX_MATCHES:
            break
        line_no = node.start_point[0]
        matches.append({
            "path": rel,
            "line": line_no + 1,
            "column": node.start_point[1] + 1,
            "kind": "declaration",
            "node_type": node.parent.type if node.parent is not None else "",
            "context": _line_context(_lines_for(project_root, rel), line_no),
        })
    emit({"ok": True, "matches": matches})


# Cache of source lines per project, so the shared walk doesn't re-read files
# for context lines. Keyed by (project_root, rel).
_LINES_CACHE = {}


def _lines_for(project_root, rel):
    key = (project_root, rel)
    if key not in _LINES_CACHE:
        full = os.path.join(project_root, rel.replace("/", os.sep))
        try:
            _LINES_CACHE[key] = read_source(full).split("\n")
        except OSError:
            _LINES_CACHE[key] = []
    return _LINES_CACHE[key]


# ---- methods: definition lookup -------------------------------------------

METHOD_NODES = {
    ".cs": {"method_declaration", "constructor_declaration", "local_function_statement"},
    ".py": {"function_definition"},
    ".js": {"function_declaration", "generator_function_declaration", "method_definition"},
    ".jsx": {"function_declaration", "generator_function_declaration", "method_definition"},
    ".ts": {"function_declaration", "generator_function_declaration", "method_definition",
            "method_signature", "abstract_method_signature"},
    ".tsx": {"function_declaration", "generator_function_declaration", "method_definition",
             "method_signature", "abstract_method_signature"},
    ".go": {"function_declaration", "method_declaration"},
    ".rs": {"function_item", "function_signature_item"},
}

# Callable ancestors used to name the caller of a reference (callers/symbol).
CALLER_NODES = {
    ".cs": {"method_declaration", "local_function_statement", "constructor_declaration",
            "property_declaration", "operator_declaration"},
    ".py": {"function_definition"},
    ".js": {"function_declaration", "generator_function_declaration", "method_definition",
            "arrow_function", "function_expression", "generator_function"},
    ".jsx": {"function_declaration", "generator_function_declaration", "method_definition",
             "arrow_function", "function_expression", "generator_function"},
    ".ts": {"function_declaration", "generator_function_declaration", "method_definition",
            "arrow_function", "function_expression", "generator_function"},
    ".tsx": {"function_declaration", "generator_function_declaration", "method_definition",
             "arrow_function", "function_expression", "generator_function"},
    ".go": {"function_declaration", "method_declaration"},
    ".rs": {"function_item", "function_signature_item", "closure_expression"},
}

# Method-like declaration kinds whose containers matter for the
# overrides/implementations view (impls/symbol).
IMPL_METHOD_KINDS = {
    ".cs": {"method_declaration", "property_declaration", "event_declaration"},
    ".py": {"function_definition"},
    ".js": {"method_definition"},
    ".jsx": {"method_definition"},
    ".ts": {"method_definition", "method_signature", "abstract_method_signature"},
    ".tsx": {"method_definition", "method_signature", "abstract_method_signature"},
    ".go": {"method_declaration"},
    ".rs": {"function_item", "function_signature_item"},
}

# Type-declaration kinds that CONTAIN members (a method's owner type).
CONTAINER_KINDS = {
    ".cs": {"class_declaration", "struct_declaration", "interface_declaration", "record_declaration"},
    ".py": {"class_definition"},
    ".js": {"class_declaration", "abstract_class_declaration"},
    ".jsx": {"class_declaration", "abstract_class_declaration"},
    ".ts": {"class_declaration", "abstract_class_declaration", "interface_declaration"},
    ".tsx": {"class_declaration", "abstract_class_declaration", "interface_declaration"},
    ".go": set(),   # Go methods carry their receiver type instead
    ".rs": {"impl_item", "trait_item"},
}

# Extra bare-keyword modifiers beyond the node_modifiers sets, per language.
EXTRA_MODIFIERS = {
    ".js": {"override", "static", "async", "get", "set"},
    ".jsx": {"override", "static", "async", "get", "set"},
    ".ts": {"override", "abstract", "static", "async", "get", "set"},
    ".tsx": {"override", "abstract", "static", "async", "get", "set"},
    ".rs": {"unsafe", "async", "const"},
}


def count_params(node, profile):
    """Number of parameters in a callable declaration. The grammar's
    parameters/formal_parameters/lambda_parameters field decides; nested
    containers (parameter_list) are unwrapped; punctuation is unnamed."""
    for fname in ("parameters", "formal_parameters", "lambda_parameters"):
        pn = node.child_by_field_name(fname)
        if pn is None:
            continue
        if pn.type in ("parameter_list", "formal_parameters"):
            return sum(1 for c in pn.children if c.is_named)
        return sum(1 for c in pn.children if c.is_named)
    return 0


def has_empty_body(node):
    """True when a callable's body is a block with no statements — a stub."""
    body = node.child_by_field_name("body")
    if body is None:
        return False
    if body.type in ("block", "statement_block"):
        return not any(c.is_named and c.type not in ("{", "}") for c in body.children)
    return False


def enclosing_caller(node, ext):
    """The nearest callable ancestor of node, or None at top level."""
    cur = node.parent
    while cur is not None:
        if cur.type in CALLER_NODES[ext]:
            return cur
        cur = cur.parent
    return None


def caller_name(caller, profile, ext):
    name = declared_name(caller, profile)
    if name:
        return name
    # Anonymous callable (arrow/closure) assigned to a named slot:
    # `const f = () => {...}` -> f; `obj.m = function(){}` -> m.
    if ext in (".js", ".jsx", ".ts", ".tsx"):
        p = caller.parent
        if p is not None and p.type in ("variable_declarator", "field_definition",
                                        "public_field_definition", "assignment_expression"):
            n2 = declared_name(p, profile)
            if n2:
                return n2
    if ext == ".rs":
        return "<closure>"
    return "<anonymous>"


def node_heritage(container, ext, profile):
    """The container's base types / interfaces / superclasses as text list."""
    out = []
    if ext == ".cs":
        bl = next((c for c in container.children if c.type == "base_list"), None)
        if bl is not None:
            for c in bl.children:
                if c.is_named:
                    out.append(c.text.decode("utf-8", errors="replace"))
    elif ext in (".js", ".jsx", ".ts", ".tsx"):
        her = next((c for c in container.children if c.type == "class_heritage"), None)
        if her is not None:
            for c in her.children:
                if c.type in ("extends_clause", "implements_clause"):
                    out.append(c.text.decode("utf-8", errors="replace"))
    elif ext == ".py":
        sc = container.child_by_field_name("superclasses")
        if sc is not None:
            for c in sc.children:
                if c.is_named:
                    out.append(c.text.decode("utf-8", errors="replace"))
    return out


def container_name(container, ext, profile):
    if ext == ".rs":
        if container.type == "impl_item":
            impl_type = container.child_by_field_name("type")
            trait = container.child_by_field_name("trait")
            tname = impl_type.text.decode("utf-8", errors="replace") if impl_type is not None else ""
            if trait is not None:
                return f"{trait.text.decode('utf-8', errors='replace')} for {tname}"
            return tname
        return declared_name(container, profile) or ""
    return declared_name(container, profile) or ""


def container_kind(container, ext):
    if ext == ".rs":
        return "impl" if container.type == "impl_item" else "trait"
    if ext == ".py":
        return "class"
    if ext in (".js", ".jsx", ".ts", ".tsx"):
        return container.type.replace("_declaration", "").replace("_delegation", "")
    return container.type.replace("_declaration", "")


def impl_entry(method_node, ext, rel, profile):
    """Override/implementation info for one method-like declaration."""
    mods = node_modifiers(method_node)
    for kw in EXTRA_MODIFIERS.get(ext, ()):
        if any(c.type == kw for c in method_node.children):
            mods.add(kw)
    if method_node.type == "method_signature":
        kind = "method"
    elif method_node.type == "abstract_method_signature":
        kind = "method"
    else:
        kind = method_node.type.replace("_declaration", "").replace("_item", "")
    entry = {
        "path": rel,
        "line": method_node.start_point[0] + 1,
        "kind": kind,
        "modifiers": sorted(mods),
        "container": "",
        "container_kind": "",
        "heritage": [],
    }
    if ext == ".go":
        entry["container"] = go_receiver_name(method_node, profile)
        entry["container_kind"] = "receiver"
        return entry
    p = method_node.parent
    while p is not None and p.type not in CONTAINER_KINDS.get(ext, ()):
        p = p.parent
    if p is not None:
        entry["container"] = container_name(p, ext, profile)
        entry["container_kind"] = container_kind(p, ext)
        entry["heritage"] = node_heritage(p, ext, profile)
    return entry


def iter_impls(project_root, symbol_name):
    """Yield impl entries for every method-like declaration named symbol_name."""
    for full_path, ext in iter_code_files(project_root):
        if ext not in IMPL_METHOD_KINDS:
            continue
        rel = os.path.relpath(full_path, project_root).replace("\\", "/")
        try:
            parser = get_parser(ext)
            source = read_source(full_path)
            tree = parser.parse(bytes(source, "utf-8"))
        except Exception:
            continue
        profile = get_profile(ext)
        kinds = IMPL_METHOD_KINDS[ext]

        def walk(node):
            if node.type in kinds:
                name = declared_name(node, profile)
                if name == symbol_name:
                    yield impl_entry(node, ext, rel, profile)
                return  # member bodies contain lambdas, not sibling members
            for child in node.children:
                yield from walk(child)

        for child in tree.root_node.children:
            yield from walk(child)


def cmd_impls(project_root, symbol_name):
    matches = []
    MAX_MATCHES = 200
    for entry in iter_impls(project_root, symbol_name):
        if len(matches) >= MAX_MATCHES:
            break
        matches.append(entry)
    emit({"ok": True, "matches": matches})


def cmd_callers(project_root, symbol_name):
    """Every usage site of symbol_name with the enclosing callable."""
    matches = []
    MAX_MATCHES = 200
    for rel, ext, node, kind in iter_ref_matches(project_root, symbol_name):
        if kind != "usage":
            continue
        if len(matches) >= MAX_MATCHES:
            break
        line_no = node.start_point[0]
        caller = enclosing_caller(node, ext)
        entry = {
            "path": rel,
            "line": line_no + 1,
            "column": node.start_point[1] + 1,
            "context": _line_context(_lines_for(project_root, rel), line_no),
        }
        if caller is not None:
            entry["caller"] = caller_name(caller, get_profile(ext), ext)
            entry["caller_line"] = caller.start_point[0] + 1
        else:
            entry["caller"] = ""
            entry["caller_line"] = 0
        matches.append(entry)
    emit({"ok": True, "matches": matches})


def cmd_symbols(project_root, substring=""):
    """Project-wide symbol table: every unique declared name and its
    definition sites (kind, path, line). Optional case-insensitive
    substring filter on the name."""
    table = {}
    MAX_ENTRIES = 4000
    for rel, ext, node in iter_declarations(project_root):
        name = node.text.decode("utf-8", errors="replace")
        if not name or (substring and substring.lower() not in name.lower()):
            continue
        sites = table.setdefault(name, [])
        if len(sites) >= 3:
            continue  # cap per-name sites; the name still counts
        sites.append({
            "path": rel,
            "line": node.start_point[0] + 1,
            "node_type": node.parent.type if node.parent is not None else "",
        })
        if len(table) >= MAX_ENTRIES:
            break
    emit({"ok": True, "symbols": table})


def iter_declarations(project_root):
    """Yield (rel, ext, node) for every declaration-site name leaf."""
    for full_path, ext in iter_code_files(project_root):
        rel = os.path.relpath(full_path, project_root).replace("\\", "/")
        try:
            parser = get_parser(ext)
            source = read_source(full_path)
            tree = parser.parse(bytes(source, "utf-8"))
        except Exception:
            continue
        profile = get_profile(ext)
        name_kinds = profile.name_identifier_kinds() | NAME_NODES

        def walk(node):
            ntype = node.type
            if ntype in name_kinds:
                if classify_name(node, node.text.decode("utf-8", errors="replace"),
                                 ext, profile) == "declaration":
                    yield rel, ext, node
                return
            for child in node.children:
                yield from walk(child)

        yield from walk(tree.root_node)


def cmd_search(project_root, name, min_params=None, max_params=None):
    """Structural search: callable definitions named `name` (or ALL callables
    when name is empty/"*") filtered by parameter count, with signature, param
    count and empty-body flag."""
    matches = []
    MAX_MATCHES = 200
    for full_path, ext in iter_code_files(project_root):
        if len(matches) >= MAX_MATCHES:
            break
        if ext not in METHOD_NODES:
            continue
        rel = os.path.relpath(full_path, project_root).replace("\\", "/")
        try:
            parser = get_parser(ext)
            source = read_source(full_path)
            tree = parser.parse(bytes(source, "utf-8"))
        except Exception:
            continue
        profile = get_profile(ext)
        method_nodes = METHOD_NODES[ext]

        def walk(node):
            if len(matches) >= MAX_MATCHES:
                return
            ntype = node.type
            is_method = ntype in method_nodes
            if not is_method and ntype in FUNCTION_VALUED_NODES:
                value = node.child_by_field_name("value")
                is_method = value is not None and value.type in ARROW_VALUE_TYPES
            if is_method:
                mname = declared_name(node, profile)
                if not mname:
                    return
                if name not in ("", "*") and mname != name:
                    return
                pc = count_params(node, profile)
                if min_params is not None and pc < min_params:
                    return
                if max_params is not None and pc > max_params:
                    return
                s, e = decl_span(node, profile)
                matches.append({
                    "path": rel,
                    "name": mname,
                    "signature": node_signature(node),
                    "start_line": s,
                    "end_line": e,
                    "lang": ext,
                    "param_count": pc,
                    "empty_body": has_empty_body(node),
                })
                return
            for child in node.children:
                walk(child)

        for child in tree.root_node.children:
            walk(child)

    emit({"ok": True, "matches": matches})


def cmd_symbol(project_root, symbol_name, min_params=None, max_params=None):
    """Combined query: definitions + callers + implementations in one walk."""
    definitions = []
    callers = []
    MAX_DEFS = 100
    MAX_CALLERS = 200

    defs_by_key = {}

    for rel, ext, node, kind in iter_ref_matches(project_root, symbol_name):
        line_no = node.start_point[0]
        if kind == "declaration":
            if len(definitions) >= MAX_DEFS:
                continue
            parent = node.parent
            entry = {
                "path": rel,
                "line": line_no + 1,
                "column": node.start_point[1] + 1,
                "end_line": node.end_point[0] + 1,
                "node_type": parent.type if parent is not None else "",
                "lang": ext,
                "context": _line_context(_lines_for(project_root, rel), line_no),
                "signature": "",
                "param_count": -1,
                "container": "",
                "heritage": [],
            }
            if parent is not None and ext in IMPL_METHOD_KINDS and parent.type in IMPL_METHOD_KINDS[ext]:
                entry["signature"] = node_signature(parent)
                entry["param_count"] = count_params(parent, get_profile(ext))
                entry["end_line"] = parent.end_point[0] + 1
                impl = impl_entry(parent, ext, rel, get_profile(ext))
                entry["container"] = impl["container"]
                entry["container_kind"] = impl["container_kind"]
                entry["heritage"] = impl["heritage"]
                entry["modifiers"] = impl["modifiers"]
            definitions.append(entry)
        else:
            if len(callers) >= MAX_CALLERS:
                continue
            caller = enclosing_caller(node, ext)
            entry = {
                "path": rel,
                "line": line_no + 1,
                "column": node.start_point[1] + 1,
                "context": _line_context(_lines_for(project_root, rel), line_no),
                "caller": "",
                "caller_line": 0,
            }
            if caller is not None:
                entry["caller"] = caller_name(caller, get_profile(ext), ext)
                entry["caller_line"] = caller.start_point[0] + 1
            callers.append(entry)

    # Structural filter for the definitions that are callables.
    if min_params is not None or max_params is not None:
        definitions = [d for d in definitions
                       if d["param_count"] >= 0
                       and (min_params is None or d["param_count"] >= min_params)
                       and (max_params is None or d["param_count"] <= max_params)]

    implementations = []
    for entry in iter_impls(project_root, symbol_name):
        if len(implementations) >= MAX_CALLERS:
            break
        implementations.append(entry)

    emit({
        "ok": True,
        "name": symbol_name,
        "definitions": definitions,
        "callers": callers,
        "implementations": implementations,
    })


def cmd_check(path):
    """Single-file syntax scan for ANY supported language: every ERROR/MISSING
    node with line/column. Backs the write_file-loop syntax feedback for
    .py/.js/.ts (ts_check.py stays the C#-specific checker)."""
    ext = os.path.splitext(path)[1].lower()
    if ext not in LANGUAGES and ext not in PACK_LANGUAGES:
        fail(f"unsupported language: {ext or '(no extension)'}")
        return
    try:
        source = read_source(path)
    except OSError as e:
        fail(f"could not read file: {e}")
        return
    try:
        tree = get_parser(ext).parse(bytes(source, "utf-8"))
    except Exception as e:
        fail(f"parse failed: {e}")
        return

    errors = []
    MAX_ERRORS = 40

    def walk_errors(node):
        if len(errors) >= MAX_ERRORS:
            return
        if node.type == "ERROR" or node.is_missing:
            raw_text = node.text.decode("utf-8", errors="replace") if node.text else ""
            text = raw_text.strip().replace("\n", "\\n")
            if len(text) > 60:
                text = text[:60] + "..."
            errors.append({
                "line": node.start_point[0] + 1,
                "column": node.start_point[1] + 1,
                "kind": "MISSING" if node.is_missing else "ERROR",
                "node_type": node.type,
                "text": text,
            })
            if node.type == "ERROR":
                return
        for child in node.children:
            walk_errors(child)

    walk_errors(tree.root_node)
    payload = {"ok": len(errors) == 0}
    if errors:
        payload["errors"] = errors
    emit(payload)


def main():
    if len(sys.argv) < 2:
        fail("usage: ts_project.py <index|refs|methods|defs|callers|impls|symbols|search|symbol|check> ...")
        return

    command = sys.argv[1]

    if command == "check":
        if len(sys.argv) < 3:
            fail("check requires a file path argument")
            return
        cmd_check(sys.argv[2])
        return

    if len(sys.argv) < 3:
        fail(f"usage: ts_project.py {command} <project_root> [...]")
        return

    project_root = sys.argv[2]

    if not os.path.isdir(project_root):
        fail(f"project root does not exist: {project_root}")
        return

    def need_arg(n, what):
        if len(sys.argv) < n + 1:
            fail(f"{what} requires a symbol_name argument")
            return None
        return sys.argv[n]

    if command == "index":
        cmd_index(project_root)
    elif command == "refs":
        a = need_arg(3, "refs")
        if a is not None:
            cmd_refs(project_root, a)
    elif command == "methods":
        a = need_arg(3, "methods")
        if a is not None:
            cmd_methods(project_root, a)
    elif command == "defs":
        a = need_arg(3, "defs")
        if a is not None:
            cmd_defs(project_root, a)
    elif command == "callers":
        a = need_arg(3, "callers")
        if a is not None:
            cmd_callers(project_root, a)
    elif command == "impls":
        a = need_arg(3, "impls")
        if a is not None:
            cmd_impls(project_root, a)
    elif command == "symbols":
        cmd_symbols(project_root, sys.argv[3] if len(sys.argv) > 3 else "")
    elif command == "search":
        # The C# host passes "name [min] [max]" as ONE argument (ArgumentList);
        # a direct CLI passes them as separate argv entries. Merge both forms.
        rest = (sys.argv[3].split() + sys.argv[4:]) if len(sys.argv) > 3 else []
        if not rest:
            fail("search requires a symbol_name argument")
            return
        min_p = int(rest[1]) if len(rest) > 1 else None
        max_p = int(rest[2]) if len(rest) > 2 else None
        cmd_search(project_root, rest[0], min_p, max_p)
    elif command == "symbol":
        rest = (sys.argv[3].split() + sys.argv[4:]) if len(sys.argv) > 3 else []
        if not rest:
            fail("symbol requires a symbol_name argument")
            return
        min_p = int(rest[1]) if len(rest) > 1 else None
        max_p = int(rest[2]) if len(rest) > 2 else None
        cmd_symbol(project_root, rest[0], min_p, max_p)
    else:
        fail(f"unknown command: {command}")


# Body/terminator children that end a declaration's signature. Everything from
# the first non-attribute child up to one of these IS the signature; the body
# itself (`block`/`statement_block`/`=>`/accessor list) is excluded.
SIGNATURE_TERMINATORS = {"block", "statement_block", "arrow_expression_clause",
                         "accessor_list", ";"}


def node_signature(node):
    """Human-readable header for a method/function declaration node, built from
    the node's CHILDREN rather than raw text slicing. Scanning text for `{`/`;`/
    `=>` breaks on default values like `s = "a;b => c{"` (the markers inside the
    string literal truncate the signature); walking children can't be fooled
    because the body is a single well-typed child."""
    text = node.text.decode("utf-8", errors="replace")
    start = node.start_byte
    end = node.end_byte
    for child in node.children:
        if child.type == "attribute_list":
            # Skip leading C# attribute lists; node byte offsets are absolute,
            # so rebase onto the node's own text slice.
            start = max(start, child.end_byte)
            continue
        if child.type in SIGNATURE_TERMINATORS:
            end = child.start_byte
            break
    return " ".join(text[start - node.start_byte: end - node.start_byte].split())


def cmd_methods(project_root, symbol_name):
    matches = []
    MAX_MATCHES = 200

    for full_path, ext in iter_code_files(project_root):
        if len(matches) >= MAX_MATCHES:
            break
        rel = os.path.relpath(full_path, project_root).replace("\\", "/")
        try:
            parser = get_parser(ext)
        except Exception:
            continue
        try:
            source = read_source(full_path)
        except OSError:
            continue
        try:
            tree = parser.parse(bytes(source, "utf-8"))
        except Exception:
            continue

        method_nodes = METHOD_NODES[ext]

        def walk(node):
            if len(matches) >= MAX_MATCHES:
                return
            ntype = node.type
            is_method = ntype in method_nodes
            if not is_method and ntype in FUNCTION_VALUED_NODES:
                value = node.child_by_field_name("value")
                is_method = value is not None and value.type in ARROW_VALUE_TYPES
            if is_method:
                name = declared_name(node)
                if name == symbol_name:
                    s, e = decl_span(node)
                    matches.append({
                        "path": rel,
                        "name": name,
                        "signature": node_signature(node),
                        "start_line": s,
                        "end_line": e,
                        "lang": ext,
                    })
            for child in node.children:
                walk(child)

        for child in tree.root_node.children:
            walk(child)

    emit({"ok": True, "matches": matches})


if __name__ == "__main__":
    main()
