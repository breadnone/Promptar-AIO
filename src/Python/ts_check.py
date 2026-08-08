#!/usr/bin/env python3
"""
Fast, build-free C# checks for the atiny.Agent write_file loop.

Parses a single .cs file with tree-sitter and reports three independent
things, all from ONE parse (one process spawn per write_file call, not one
per check):

  1. SYNTAX ERRORS — every ERROR / MISSING node tree-sitter's error-recovery
     parser finds. Catches brace/paren/semicolon-class mistakes in
     milliseconds, no MSBuild/restore/compile involved.

  2. PLACEHOLDERS — signs the model cut a corner instead of finishing the
     job: TODO/FIXME/"rest of the code"-style comments, `throw new
     NotImplementedException()`, and empty method/constructor bodies.

  3. DEAD PRIVATE MEMBERS — a private field/method/property whose declared
     name appears exactly once in this file (i.e. only at its own
     declaration, never referenced). SAME-FILE SCOPE ONLY: a `private`
     member of a `partial class` can legitimately be used from another file
     of that same partial class, which this cannot see — treat this as a
     lead to check, not a verdict, especially for partial classes.

None of this is a substitute for a real build: tree-sitter only knows
grammar, not semantics. CS0103/CS1061/etc. still need an actual compile.
Checks 2 and 3 are skipped if the file has ERROR-kind syntax problems (as
opposed to MISSING-kind, e.g. one dropped semicolon) — once the parser has
desynced, iterating "empty bodies" or "unused members" over the wreckage
produces noise, not signal. Fix the ERROR-kind issues first.

One-time setup (the Windows embeddable Python zip ships without pip):
    Python\\python.exe get-pip.py
    Python\\python.exe -m pip install tree-sitter tree-sitter-c-sharp

Usage:
    python ts_check.py <path-to-file.cs>

Always exits 0. Always prints exactly one line of JSON to stdout:
    {"ok": true}
    {"ok": false, "errors": [...]}
    {"ok": true, "placeholders": [...], "dead_code": [...]}
    {"ok": null, "reason": "..."}   <- checker itself couldn't run (this is
                                        NOT a verdict on the file either way)
"""
import sys
import re
import json
from collections import Counter

PLACEHOLDER_COMMENT_RE = re.compile(
    r"(\.\.\.|TODO\b|FIXME\b|XXX\b|rest of (the )?code|existing code|"
    r"implementation (goes|here)|placeholder|to be implemented|"
    r"left as an exercise)",
    re.IGNORECASE,
)

ACCESS_MODIFIERS = {"public", "private", "protected", "internal"}
MAX_PLACEHOLDERS = 30
MAX_DEAD = 30


def emit(payload):
    print(json.dumps(payload))
    sys.exit(0)


def fail(reason):
    emit({"ok": None, "reason": reason})


def node_modifiers(node):
    mods = set()
    for child in node.children:
        if child.type == "modifier":
            for gc in child.children:
                mods.add(gc.type)
    return mods


def declared_name(node):
    for child in node.children:
        if child.type == "identifier":
            return child.text.decode("utf-8", errors="replace")
    return None


def find_placeholders(root):
    findings = []

    def walk(node):
        if len(findings) >= MAX_PLACEHOLDERS:
            return

        if node.type == "comment":
            text = node.text.decode("utf-8", errors="replace")
            if PLACEHOLDER_COMMENT_RE.search(text):
                snippet = text.strip()
                if len(snippet) > 80:
                    snippet = snippet[:80] + "..."
                findings.append({
                    "line": node.start_point[0] + 1,
                    "column": node.start_point[1] + 1,
                    "kind": "todo_comment",
                    "detail": snippet,
                })

        elif node.type == "object_creation_expression":
            for c in node.children:
                if c.type in ("identifier", "generic_name", "qualified_name"):
                    if "NotImplementedException" in c.text.decode("utf-8", errors="replace"):
                        findings.append({
                            "line": node.start_point[0] + 1,
                            "column": node.start_point[1] + 1,
                            "kind": "not_implemented",
                            "detail": "throws NotImplementedException",
                        })
                        break

        elif node.type in ("method_declaration", "constructor_declaration"):
            block = next((c for c in node.children if c.type == "block"), None)
            if block is not None:
                stmt_children = [c for c in block.children if c.type not in ("{", "}")]
                if not stmt_children:
                    name = declared_name(node) or "?"
                    findings.append({
                        "line": node.start_point[0] + 1,
                        "column": node.start_point[1] + 1,
                        "kind": "empty_body",
                        "detail": f"'{name}' has an empty body",
                    })

        for c in node.children:
            walk(c)

    walk(root)
    return findings


def find_dead_private_members(root):
    """SAME-FILE ONLY — see module docstring caveat about partial classes."""
    members = []  # (name, line, column, kind)

    def collect(node, container_kind):
        for child in node.children:
            ctype = child.type

            if ctype in ("class_declaration", "struct_declaration", "record_declaration"):
                collect(child, ctype)
            elif ctype == "interface_declaration":
                continue  # interface members are implicitly public — not applicable
            elif ctype in ("namespace_declaration", "declaration_list"):
                collect(child, container_kind)
            elif container_kind and ctype in ("method_declaration", "property_declaration"):
                mods = node_modifiers(child)
                is_private = "private" in mods or not (mods & ACCESS_MODIFIERS)
                if is_private:
                    name = declared_name(child)
                    if name:
                        members.append((
                            name, child.start_point[0] + 1, child.start_point[1] + 1,
                            "method" if ctype == "method_declaration" else "property",
                        ))
            elif container_kind and ctype == "field_declaration":
                mods = node_modifiers(child)
                is_private = "private" in mods or not (mods & ACCESS_MODIFIERS)
                if is_private:
                    var_decl = next((c for c in child.children if c.type == "variable_declaration"), None)
                    if var_decl is not None:
                        for vc in var_decl.children:
                            if vc.type == "variable_declarator":
                                name = declared_name(vc)
                                if name:
                                    members.append((
                                        name, child.start_point[0] + 1, child.start_point[1] + 1, "field",
                                    ))
            # constructors/operators/events deliberately excluded — too easy to
            # false-positive (factory patterns, event wiring) for a heuristic tool.

    collect(root, None)
    if not members:
        return []

    counts = Counter()

    def count_walk(node):
        if node.type == "identifier":
            counts[node.text.decode("utf-8", errors="replace")] += 1
        for c in node.children:
            count_walk(c)

    count_walk(root)

    dead = []
    for name, line, col, kind in members:
        if counts.get(name, 0) <= 1 and len(dead) < MAX_DEAD:
            dead.append({"line": line, "column": col, "name": name, "kind": kind})
    return dead


def main():
    if len(sys.argv) < 2:
        fail("no file path given")

    path = sys.argv[1]

    try:
        with open(path, "r", encoding="utf-8-sig", errors="replace") as f:
            source = f.read()
    except OSError as e:
        fail(f"could not read file: {e}")
        return

    try:
        import tree_sitter_c_sharp as tscsharp
        from tree_sitter import Language, Parser
    except ImportError as e:
        fail(
            f"tree-sitter not installed in the embedded interpreter ({e}). "
            "Run: Python\\python.exe -m pip install tree-sitter tree-sitter-c-sharp"
        )
        return

    try:
        cs_language = Language(tscsharp.language())
        parser = Parser(cs_language)
    except Exception as e:
        fail(f"failed to load the C# grammar: {e}")
        return

    try:
        tree = parser.parse(bytes(source, "utf-8"))
    except Exception as e:
        fail(f"parse failed: {e}")
        return

    errors = []
    MAX_ERRORS = 40  # cap so one badly-broken file can't flood the tool result

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

    # Skip placeholder/dead-code scanning once the parser has genuinely desynced
    # (an ERROR node, not just a MISSING token) — results over a mangled subtree
    # are noise. A lone MISSING token (e.g. one dropped ';') still leaves the
    # rest of the tree meaningful, so those checks still run.
    has_error_kind = any(e["kind"] == "ERROR" for e in errors)
    if not has_error_kind:
        placeholders = find_placeholders(tree.root_node)
        dead_code = find_dead_private_members(tree.root_node)
        if placeholders:
            payload["placeholders"] = placeholders
        if dead_code:
            payload["dead_code"] = dead_code

    emit(payload)


if __name__ == "__main__":
    main()
