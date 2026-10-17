"""Static checks for the WPF views, runnable without Windows.

WPF reports most XAML mistakes only at run time (a missing resource key or a style applied to
the wrong control type crashes the app on start). This catches those before CI's Windows build:

  - every file is well-formed XML
  - every {StaticResource X} / {DynamicResource X} has a matching x:Key
  - Style="{StaticResource X}" is used on an element its TargetType allows
  - event handlers named in XAML exist in the code-behind
  - the first segment of each {Binding Path} is a public property of some view-model type
  - x:Name values used from code-behind exist in the XAML

Usage: python tests/ui/lint_xaml.py app/NovaMotion.App app/NovaMotion.Core
"""
import glob
import os
import re
import sys
import xml.etree.ElementTree as ET

app_dir, core_dir = sys.argv[1], sys.argv[2]
X = "{http://schemas.microsoft.com/winfx/2006/xaml}"
errors = []

# WPF types we use and their bases (enough to check style target types)
BASES = {
    "RadioButton": "ToggleButton", "CheckBox": "ToggleButton", "ToggleButton": "ButtonBase",
    "Button": "ButtonBase", "RepeatButton": "ButtonBase", "ButtonBase": "ContentControl",
    "ContentControl": "Control", "UserControl": "ContentControl", "Window": "ContentControl",
    "ToolTip": "ContentControl", "Label": "ContentControl", "ScrollViewer": "ContentControl",
    "TextBox": "TextBoxBase", "TextBoxBase": "Control", "Slider": "RangeBase", "ProgressBar": "RangeBase",
    "ScrollBar": "RangeBase", "RangeBase": "Control", "Thumb": "Control", "ComboBox": "ItemsControl",
    "ItemsControl": "Control", "ContextMenu": "MenuBase", "MenuBase": "ItemsControl", "MenuItem": "HeaderedItemsControl",
    "HeaderedItemsControl": "ItemsControl", "Control": "FrameworkElement",
    "TextBlock": "FrameworkElement", "Border": "Decorator", "Decorator": "FrameworkElement",
    "Grid": "Panel", "StackPanel": "Panel", "Canvas": "Panel", "WrapPanel": "Panel", "DockPanel": "Panel",
    "UniformGrid": "Panel", "Panel": "FrameworkElement", "Path": "Shape", "Ellipse": "Shape", "Shape": "FrameworkElement",
    "FrameworkElement": "UIElement", "Track": "FrameworkElement",
}


def is_a(t, target):
    while t:
        if t == target:
            return True
        t = BASES.get(t)
    return False


def local(tag):
    return tag.split("}")[-1]


files = sorted(glob.glob(os.path.join(app_dir, "**", "*.xaml"), recursive=True))
trees = {}
for f in files:
    try:
        trees[f] = ET.parse(f)
    except ET.ParseError as e:
        errors.append(f"{f}: not well-formed: {e}")

# ---- resource keys and style target types
keys, style_types = set(), {}
for f, t in trees.items():
    for el in t.iter():
        k = el.get(X + "Key")
        if k:
            keys.add(k)
            if local(el.tag) == "Style":
                style_types[k] = (el.get("TargetType") or "").split(":")[-1].strip("{}").replace("x:Type ", "")

res_re = re.compile(r"\{(?:StaticResource|DynamicResource)\s+([^}\s]+)\}")
for f, t in trees.items():
    for el in t.iter():
        for attr, val in el.attrib.items():
            for k in res_re.findall(val):
                if k.startswith("{"):
                    continue
                if k not in keys:
                    errors.append(f"{os.path.basename(f)}: unknown resource '{k}' on <{local(el.tag)} {local(attr)}>")
        style = el.get("Style")
        m = re.fullmatch(r"\{StaticResource\s+([^}\s]+)\}", style or "")
        if m and m.group(1) in style_types:
            target = style_types[m.group(1)]
            tag = local(el.tag)
            if target and tag in BASES and not is_a(tag, target):
                errors.append(f"{os.path.basename(f)}: style '{m.group(1)}' (TargetType {target}) used on <{tag}>")
        based = el.get("BasedOn")
        m = re.fullmatch(r"\{StaticResource\s+([^}\s]+)\}", based or "")
        if m and m.group(1) in style_types and local(el.tag) == "Style":
            child = (el.get("TargetType") or "").strip()
            parent = style_types[m.group(1)]
            if child and parent and child in BASES and not is_a(child, parent):
                errors.append(f"{os.path.basename(f)}: style {el.get(X + 'Key')} ({child}) is BasedOn {m.group(1)} ({parent})")

# ---- event handlers and names in code-behind
EVENTS = {"Click", "KeyDown", "PreviewKeyDown", "SelectionChanged", "Loaded", "MouseDown", "MouseLeftButtonDown",
          "Checked", "Unchecked", "TextChanged", "Drop", "MouseMove"}
for f, t in trees.items():
    cs = f + ".cs"
    root = t.getroot()
    if not root.get(X + "Class"):
        continue
    code = open(cs, encoding="utf-8").read() if os.path.exists(cs) else ""
    names = {el.get(X + "Name") for el in t.iter() if el.get(X + "Name")}
    for el in t.iter():
        for attr, val in el.attrib.items():
            if local(attr) in EVENTS and not val.startswith("{"):
                if not re.search(r"\b" + re.escape(val) + r"\s*\(", code):
                    errors.append(f"{os.path.basename(f)}: handler {val} for {local(attr)} missing from code-behind")
    # identifiers used as fields in the code-behind that look like element names
    for ident in set(re.findall(r"\b([A-Z][A-Za-z]+)\.(?:Visibility|IsVisible|ActualWidth|ActualHeight|Text|Content|Padding|"
                                r"BeginAnimation|IsMouseOver|Focus|Fill|ShowSection|IsChecked|UpdateLayout|HoverChanged|"
                                r"VideoHandle|SizeChanged|Cursor|MouseMove|MouseLeftButtonDown|MouseWheel|MouseLeave|FocusLink)\b", code)):
        if ident not in names and ident not in {"Visibility", "Keyboard", "Mouse", "Cursors", "Application", "Canvas", "SystemParameters",
                                                "TranslateTransform", "Math", "Path", "File", "Directory", "DataFormats",
                                                "Clipboard", "Dispatcher", "WindowState", "Forms", "Screen"}:
            errors.append(f"{os.path.basename(cs)}: uses '{ident}' but the XAML has no x:Name=\"{ident}\"")

# ---- binding names against view-model properties
props = set()
for cs in glob.glob(os.path.join(core_dir, "**", "*.cs"), recursive=True):
    src = open(cs, encoding="utf-8").read()
    props.update(re.findall(r"public\s+(?:static\s+)?(?:readonly\s+)?[\w<>\[\],.?()\s]+?\s(\w+)\s*(?:\{|=>|=)", src))
    # record positional parameters
    for rec in re.findall(r"record\s+\w+\(([^)]*)\)", src):
        props.update(re.findall(r"(\w+)\s*(?:,|$|=)", rec.replace("\n", " ")))
WPF_PROPS = {"IsChecked", "DataContext", "PlacementTarget", "Tag", "ActualWidth", "ActualHeight", "IsDropDownOpen", "Content",
             "IsMouseOver", "Text"}
bind_re = re.compile(r"\{Binding(?:\s+(?:Path=)?([A-Za-z_][\w.]*))?")
for f, t in trees.items():
    for el in t.iter():
        for attr, val in el.attrib.items():
            for m in bind_re.finditer(val):
                path = m.group(1)
                if not path:
                    continue
                first = path.split(".")[0]
                if first in ("Path", "RelativeSource", "ElementName", "Converter", "Mode", "StringFormat"):
                    continue
                if first not in props and first not in WPF_PROPS:
                    errors.append(f"{os.path.basename(f)}: binding '{path}' matches no view-model property")
        if local(el.tag) == "Binding" and el.get("Path"):
            first = el.get("Path").split(".")[0]
            if first not in props and first not in WPF_PROPS:
                errors.append(f"{os.path.basename(f)}: binding '{el.get('Path')}' matches no view-model property")

for e in errors:
    print("FAIL ", e)
print(f"{len(files)} XAML files, {len(keys)} resource keys, {len(errors)} problems")
sys.exit(1 if errors else 0)
