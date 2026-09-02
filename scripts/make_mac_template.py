#!/usr/bin/env python3
"""
Derive a macOS-friendly template from a MetaMAP template that uses MetaFETCH.

The map picker relies on an embedded web view; on machines where that is not
available the user types the coordinates instead. This script removes the
MetaFETCH component (and the button/group driving it) and replaces it with two
text panels wired into every input that used to read MetaFETCH's Latitude /
Longitude outputs - the same transformation the hand-made
MetaMAP_advanced_for_MACOS.ghx applies to MetaMAP_advanced.ghx.

Usage: make_mac_template.py <input.ghx> <output.ghx>
"""
import re
import sys
import uuid

OBJECT_RE = re.compile(r'(\s*)<chunk name="Object" index="(\d+)">.*?\n\1</chunk>', re.S)


def item(name, value, type_name="gh_string", type_code="10", index=None):
    idx = f' index="{index}"' if index is not None else ""
    return f'<item name="{name}"{idx} type_name="{type_name}" type_code="{type_code}">{value}</item>'


def find(text, pattern):
    m = re.search(pattern, text)
    return m.group(1) if m else None


def panel_chunk(instance_guid, nickname, text, x, y):
    return f'''            <chunk name="Object" index="__INDEX__">
              <items count="2">
                <item name="GUID" type_name="gh_guid" type_code="9">59e0b89a-e487-49f8-bab8-b5bab16be14c</item>
                <item name="Name" type_name="gh_string" type_code="10">Panel</item>
              </items>
              <chunks count="1">
                <chunk name="Container">
                  <items count="8">
                    <item name="Description" type_name="gh_string" type_code="10">A panel for custom notes and text values</item>
                    <item name="InstanceGuid" type_name="gh_guid" type_code="9">{instance_guid}</item>
                    <item name="Name" type_name="gh_string" type_code="10">Panel</item>
                    <item name="NickName" type_name="gh_string" type_code="10">{nickname}</item>
                    <item name="Optional" type_name="gh_bool" type_code="1">false</item>
                    <item name="ScrollRatio" type_name="gh_double" type_code="6">0</item>
                    <item name="SourceCount" type_name="gh_int32" type_code="3">0</item>
                    <item name="UserText" type_name="gh_string" type_code="10">{text}</item>
                  </items>
                  <chunks count="2">
                    <chunk name="Attributes">
                      <items count="5">
                        <item name="Bounds" type_name="gh_drawing_rectanglef" type_code="35">
                          <X>{x}</X>
                          <Y>{y}</Y>
                          <W>155</W>
                          <H>38</H>
                        </item>
                        <item name="MarginLeft" type_name="gh_int32" type_code="3">0</item>
                        <item name="MarginRight" type_name="gh_int32" type_code="3">0</item>
                        <item name="MarginTop" type_name="gh_int32" type_code="3">0</item>
                        <item name="Pivot" type_name="gh_drawing_pointf" type_code="31">
                          <X>{x}</X>
                          <Y>{y}</Y>
                        </item>
                      </items>
                    </chunk>
                    <chunk name="PanelProperties">
                      <items count="7">
                        <item name="Colour" type_name="gh_drawing_color" type_code="36">
                          <ARGB>255;255;250;90</ARGB>
                        </item>
                        <item name="DrawIndices" type_name="gh_bool" type_code="1">true</item>
                        <item name="DrawPaths" type_name="gh_bool" type_code="1">true</item>
                        <item name="Multiline" type_name="gh_bool" type_code="1">true</item>
                        <item name="SpecialCodes" type_name="gh_bool" type_code="1">false</item>
                        <item name="Stream" type_name="gh_bool" type_code="1">false</item>
                        <item name="Wrap" type_name="gh_bool" type_code="1">true</item>
                      </items>
                    </chunk>
                  </chunks>
                </chunk>
              </chunks>
            </chunk>
'''


def group_chunk(instance_guid, member_ids, nickname):
    ids = "\n".join(
        f'                    <item name="ID" index="{i}" type_name="gh_guid" type_code="9">{g}</item>'
        for i, g in enumerate(member_ids)
    )
    return f'''            <chunk name="Object" index="__INDEX__">
              <items count="2">
                <item name="GUID" type_name="gh_guid" type_code="9">c552a431-af5b-46a9-a8a4-0fcbc27ef596</item>
                <item name="Name" type_name="gh_string" type_code="10">Group</item>
              </items>
              <chunks count="1">
                <chunk name="Container">
                  <items count="{7 + len(member_ids)}">
                    <item name="Border" type_name="gh_int32" type_code="3">3</item>
                    <item name="Colour" type_name="gh_drawing_color" type_code="36">
                      <ARGB>150;255;0;0</ARGB>
                    </item>
                    <item name="Description" type_name="gh_string" type_code="10">A group of Grasshopper objects</item>
{ids}
                    <item name="ID_Count" type_name="gh_int32" type_code="3">{len(member_ids)}</item>
                    <item name="InstanceGuid" type_name="gh_guid" type_code="9">{instance_guid}</item>
                    <item name="Name" type_name="gh_string" type_code="10">Group</item>
                    <item name="NickName" type_name="gh_string" type_code="10">{nickname}</item>
                  </items>
                  <chunks count="1">
                    <chunk name="Attributes" />
                  </chunks>
                </chunk>
              </chunks>
            </chunk>
'''


def main(src, dst, default_lat="41.041122", default_lon="28.989991"):
    text = open(src, encoding="utf-8-sig").read()

    objects = list(OBJECT_RE.finditer(text))
    if not objects:
        sys.exit("no objects found")

    fetch = None
    for m in objects:
        if item("Name", "MetaFETCH") in m.group(0):
            fetch = m
            break
    if fetch is None:
        sys.exit("template has no MetaFETCH component")

    fetch_xml = fetch.group(0)
    fetch_guid = find(fetch_xml, r'<item name="InstanceGuid"[^>]*>([0-9a-f-]+)</item>')
    fetch_x = float(find(fetch_xml, r'<chunk name="Attributes">\s*<items count="2">\s*<item name="Bounds"[^>]*>\s*<X>([-\d.]+)</X>'))
    fetch_y = float(find(fetch_xml, r'<chunk name="Attributes">\s*<items count="2">\s*<item name="Bounds"[^>]*>\s*<X>[-\d.]+</X>\s*<Y>([-\d.]+)</Y>'))

    # Output parameter instance guids (Latitude, Longitude) and the button feeding "Show Map".
    outputs = re.findall(r'<chunk name="param_output" index="\d+">\s*<items count="\d+">.*?</chunk>', fetch_xml, re.S)
    lat_out = lon_out = None
    for o in outputs:
        guid = find(o, r'<item name="InstanceGuid"[^>]*>([0-9a-f-]+)</item>')
        name = find(o, r'<item name="Name"[^>]*>([^<]*)</item>')
        if name == "Latitude":
            lat_out = guid
        elif name == "Longitude":
            lon_out = guid
    if not (lat_out and lon_out):
        sys.exit("could not find MetaFETCH outputs")

    m = re.search(r'<chunk name="param_input" index="0">.*?<item name="Source" index="0"[^>]*>([0-9a-f-]+)</item>', fetch_xml, re.S)
    show_map_source = m.group(1) if m else None

    remove = {fetch_guid}
    if show_map_source:
        remove.add(show_map_source)

    # Groups that only contain removed objects (or stale ids) are removed as well;
    # surviving groups lose any member id that no longer exists.
    all_ids = {find(o.group(0), r'<item name="InstanceGuid"[^>]*>([0-9a-f-]+)</item>') for o in objects}
    kept = []
    for m in objects:
        xml = m.group(0)
        guid = find(xml, r'<item name="InstanceGuid"[^>]*>([0-9a-f-]+)</item>')
        if guid in remove:
            continue
        if item("Name", "Group") in xml:
            ids = re.findall(r'<item name="ID" index="\d+"[^>]*>([0-9a-f-]+)</item>', xml)
            live = [i for i in ids if i in all_ids and i not in remove]
            if not live:
                continue
            if len(live) != len(ids):
                xml = re.sub(r'\s*<item name="ID" index="\d+"[^>]*>[0-9a-f-]+</item>', '', xml)
                new_ids = "".join(
                    f'\n                    <item name="ID" index="{i}" type_name="gh_guid" type_code="9">{g}</item>'
                    for i, g in enumerate(live))
                xml = xml.replace('<item name="Description" type_name="gh_string" type_code="10">A group of Grasshopper objects</item>',
                                  '<item name="Description" type_name="gh_string" type_code="10">A group of Grasshopper objects</item>' + new_ids)
                xml = re.sub(r'<item name="ID_Count" type_name="gh_int32" type_code="3">\d+</item>',
                             f'<item name="ID_Count" type_name="gh_int32" type_code="3">{len(live)}</item>', xml)
                old_count = int(find(xml, r'<chunk name="Container">\s*<items count="(\d+)">'))
                xml = re.sub(r'(<chunk name="Container">\s*<items count=")\d+(")',
                             rf'\g<1>{old_count - (len(ids) - len(live))}\g<2>', xml, count=1)
        kept.append(xml)

    lat_panel = str(uuid.uuid4())
    lon_panel = str(uuid.uuid4())
    group = str(uuid.uuid4())

    # Rewire consumers of the old outputs to the new panels.
    rewired = []
    for xml in kept:
        xml = xml.replace(
            f'<item name="Source" index="0" type_name="gh_guid" type_code="9">{lat_out}</item>',
            f'<item name="Source" index="0" type_name="gh_guid" type_code="9">{lat_panel}</item>')
        xml = xml.replace(
            f'<item name="Source" index="0" type_name="gh_guid" type_code="9">{lon_out}</item>',
            f'<item name="Source" index="0" type_name="gh_guid" type_code="9">{lon_panel}</item>')
        rewired.append(xml)

    rewired.append(panel_chunk(lat_panel, "Latitude", default_lat, fetch_x, fetch_y - 10))
    rewired.append(panel_chunk(lon_panel, "Longitude", default_lon, fetch_x, fetch_y + 40))
    rewired.append(group_chunk(group, [lat_panel, lon_panel], "Define it manually"))

    # Re-index.
    final = []
    for i, xml in enumerate(rewired):
        xml = re.sub(r'<chunk name="Object" index="\d+">', f'<chunk name="Object" index="{i}">', xml, count=1)
        xml = xml.replace("__INDEX__", str(i))
        final.append(xml.rstrip("\n"))

    start = objects[0].start()
    end = objects[-1].end()
    body = "\n".join(final)
    new_text = text[:start] + "\n" + body + text[end:]
    new_text = re.sub(r'(<item name="ObjectCount" type_name="gh_int32" type_code="3">)\d+(</item>)',
                      rf'\g<1>{len(final)}\g<2>', new_text)

    src_name = src.replace("\\", "/").split("/")[-1]
    dst_name = dst.replace("\\", "/").split("/")[-1]
    new_text = new_text.replace(item("Name", src_name), item("Name", dst_name))

    with open(dst, "w", encoding="utf-8-sig", newline="\n") as f:
        f.write(new_text)
    print(f"{dst}: {len(final)} objects (removed {len(objects) - len(kept)}, added 3)")


if __name__ == "__main__":
    if len(sys.argv) != 3:
        sys.exit(__doc__)
    main(sys.argv[1], sys.argv[2])
