"""Generate model feature facts from official Xiaomi Earbuds regional catalogs."""
import argparse
import json
import pathlib
import re

p = argparse.ArgumentParser(description=__doc__)
p.add_argument('--research', type=pathlib.Path, required=True)
p.add_argument('--output', type=pathlib.Path, required=True)
a = p.parse_args()
models = {}
for path in sorted(a.research.glob('xiaomi-products-*.json')):
    for row in json.loads(path.read_text('utf-8'))['result']['tw_product_list']:
        facts = {}
        for feature in row.get('func_list', []):
            if feature['func_id'] not in (1002, 1005, 2016): continue
            try:
                extra = json.loads(re.sub(r',\s*([}\]])', r'\1', feature.get('extra_data') or '{}'))
            except json.JSONDecodeError:
                # A malformed catalog entry must not grant controls.
                extra = {}
            for fid, key, role in [(1002, 'tws_gear', 'noise'), (1005, 'tws_gear', 'ambient'), (2016, 'sound_effect', 'eq')]:
                if feature['func_id'] == fid:
                    values = extra.get(key, [])
                    if role == 'eq' and isinstance(values, dict): values = values.get('effects', [])
                    facts[role] = {int(v) for v in values if isinstance(v, int) and 0 <= v <= 255 and (role != 'eq' or v != 10)}
        name = row['name']
        normalized = ''.join(c for c in name.upper() if c.isalnum())
        if normalized not in models: models[normalized] = (name, {k: facts.get(k, set()) for k in ['noise', 'ambient', 'eq']})
        else:
            for k in models[normalized][1]: models[normalized][1][k] &= facts.get(k, set())
lines = []
for name, facts in sorted(models.values()):
    arrays = [f'[{", ".join(str(v) for v in sorted(facts[k]))}]' for k in ['noise', 'ambient', 'eq']]
    lines.append(f'        new({json.dumps(name, ensure_ascii=True)}, {", ".join(arrays)}),')
a.output.write_text('// SPDX-License-Identifier: GPL-3.0-only\n// Generated from official Xiaomi Earbuds regional product catalogs.\nnamespace EncoDeck.Protocol.Brands.Xiaomi;\ninternal sealed record XiaomiModelFeatures(string Name, byte[] NoiseLevels, byte[] AmbientLevels, byte[] Equalizers)\n{\n    internal static XiaomiModelFeatures? Find(string? name) => All.FirstOrDefault(m => Normalize(m.Name) == Normalize(name));\n    private static string Normalize(string? name) => new((name ?? "").ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());\n    private static readonly XiaomiModelFeatures[] All =\n    [\n' + '\n'.join(lines) + '\n    ];\n}\n', 'utf-8')
a.output.write_text(a.output.read_text('utf-8').replace('namespace EncoDeck.', '#nullable enable\nnamespace EncoDeck.'), 'utf-8')
print('Generated', len(models), 'reviewed model feature definitions')
