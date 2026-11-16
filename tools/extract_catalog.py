"""Read effect metadata from the backend without importing or executing it."""
import ast
import json
import re
import sys
from pathlib import Path

source, destination = map(Path, sys.argv[1:3])
tree = ast.parse(source.read_text(encoding="utf-8-sig"))
trees = [tree]
for filename in ("ambient.py", "power.py"):
    effect_source = source.with_name(filename)
    if effect_source.is_file():
        trees.append(ast.parse(effect_source.read_text(encoding="utf-8-sig")))
base_tree = ast.parse(source.with_name("base.py").read_text(encoding="utf-8-sig"))


def read_param(call):
    values = [ast.literal_eval(arg) for arg in call.args]
    # Parameters may leave out the default, so there can be fewer values than names.
    param = dict(zip(("key", "label", "type", "default"), values, strict=False))
    for kw in call.keywords:
        try:
            param[kw.arg] = ast.literal_eval(kw.value)
        except ValueError:
            if kw.arg == "choices" and isinstance(kw.value, ast.Call) and isinstance(kw.value.args[0], ast.Name):
                palette_name = kw.value.args[0].id
                palette = next(n.value for n in tree.body if isinstance(n, ast.AnnAssign)
                               and isinstance(n.target, ast.Name) and n.target.id == palette_name)
                param[kw.arg] = [ast.literal_eval(k) for k in palette.keys]
            else:
                raise
    return param


transition = next(read_param(node.value) for node in base_tree.body if isinstance(node, ast.Assign)
                  and any(isinstance(target, ast.Name) and target.id == "COLOR_TRANSITION" for target in node.targets))
base_effect = next(node for node in base_tree.body if isinstance(node, ast.ClassDef) and node.name == "Effect")
category = next(ast.literal_eval(node.value) for node in base_effect.body if isinstance(node, ast.AnnAssign)
                and isinstance(node.target, ast.Name) and node.target.id == "category")
catalog = []
for cls in [node for effect_tree in trees for node in effect_tree.body]:
    if not isinstance(cls, ast.ClassDef):
        continue
    item = {"category": category, "params": []}
    for statement in cls.body:
        if not isinstance(statement, ast.Assign) or not isinstance(statement.targets[0], ast.Name):
            continue
        key = statement.targets[0].id
        if key in ("id", "name", "category", "description", "exclusive_params"):
            item[key] = ast.literal_eval(statement.value)
        elif key == "params":
            item["params"] = [read_param(call) for call in statement.value.elts]
    if "id" in item:
        colors = [param for param in item["params"] if param["type"] == "color"]
        keys = {param["key"] for param in item["params"]}
        if colors:
            assert "color_transition" not in keys
            for param in colors:
                random_key = f"random_{param['key']}"
                assert random_key not in keys
                param["random_param"] = random_key
                item["params"].append({"key": random_key, "label": f"Randomize {param['label'].lower()}",
                                       "type": "bool", "default": False, "randomizes": param["key"]})
            for param in colors:
                brightness_key = f"brightness_{param['key']}"
                assert brightness_key not in keys
                param["brightness_param"] = brightness_key
                item["params"].append({"key": brightness_key, "label": f"{param['label']} brightness",
                                       "type": "float", "default": 100.0, "min": 0.0, "max": 100.0,
                                       "step": 1.0, "unit": "%", "dims": param["key"]})
            item["params"].append(dict(transition))
        catalog.append(item)
AMERICAN = {"colour": "color", "centre": "center", "behaviour": "behavior", "grey": "gray"}


def american(match):
    word = AMERICAN[match.group().lower()]
    return word.capitalize() if match.group()[0].isupper() else word


serialized = re.sub("|".join(AMERICAN), american, json.dumps(catalog, indent=2), flags=re.IGNORECASE)
destination.write_text("export const CATALOG = " + serialized + ";\n", encoding="utf-8", newline="\n")
print(f"Extracted {len(catalog)} effect definitions.")
