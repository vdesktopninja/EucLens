#!/usr/bin/env python3
"""Extract the WPF resource contract; no npm, secrets, or generated folders read.

First write: python3 tools/ux-contract.py
Refresh (after informing the operator): python3 tools/ux-contract.py --refresh
Validate committed extraction: python3 tools/ux-contract.py --check
"""
import argparse
import json
from pathlib import Path
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / '.uxproof'
APP = ROOT / 'src/EucLens.App'
X = '{http://schemas.microsoft.com/winfx/2006/xaml}'
START, END = '<!-- uxproof:manual-start -->', '<!-- uxproof:manual-end -->'


def extract():
    tokens, components, sources = [], [], []
    for file in sorted((APP / 'Themes').glob('*.xaml')):
        source = file.relative_to(ROOT).as_posix()
        count = 0
        for item in ET.parse(file).getroot():
            tag = item.tag.split('}')[-1]
            key = item.get(X + 'Key')
            if tag in ('Style', 'DataTemplate'):
                name = key or f"Implicit{item.get('TargetType')}Style"
                components.append({'name': name, 'file': source, 'root': 'src/EucLens.App/Themes',
                                   'kind': tag, 'targetType': item.get('TargetType')})
            elif key:
                value = item.get('Color') if tag == 'SolidColorBrush' else (item.text or '').strip()
                kind = ('alias' if value.startswith('{') else 'color') if tag in ('Color', 'SolidColorBrush') else 'font' if tag == 'FontFamily' else 'size'
                tokens.append({'name': key, 'value': value, 'kind': kind, 'source': source,
                               'resourceType': tag, 'theme': file.stem if file.stem in ('Light', 'Dark') else None})
                count += 1
        sources.append({'file': source, 'tokenCount': count})
    archetypes = []
    for name, kind in [('Dashboard', 'session-overview'), ('Diagnostics', 'diagnostic-evidence'),
                       ('Recorder', 'recording-and-export'), ('System', 'technical-inventory'),
                       ('Advanced', 'raw-metric-inspection'), ('Settings', 'preferences')]:
        source = f'src/EucLens.App/Views/{name}View.xaml'
        ET.parse(ROOT / source)
        components.append({'name': f'{name}View', 'file': source, 'root': 'src/EucLens.App/Views', 'kind': 'UserControl'})
        archetypes.append({'kind': kind, 'count': 1, 'examples': [source], 'state': 'implemented; Windows visual acceptance pending'})
    components.append({'name': 'MainWindow', 'file': 'src/EucLens.App/MainWindow.xaml', 'root': 'src/EucLens.App', 'kind': 'Window'})
    contract = {
        'version': 1, 'generatedBy': 'tools/ux-contract.py (WPF adapter; uxproof@0.3.1 shape)',
        'framework': 'WPF / .NET 10 / MVVM', 'workspace': True,
        'styling': {'system': 'WPF merged resource dictionaries', 'tailwind': None, 'tokenSources': sources},
        'libraries': {'charts': ['LiveChartsCore.SkiaSharpView.WPF 2.0.0'], 'icons': ['Segoe MDL2 Assets']},
        'componentRoots': [{'dir': 'src/EucLens.App/Themes', 'fileCount': 4}, {'dir': 'src/EucLens.App/Views', 'fileCount': 6}],
        'nativeEquivalents': [{'element': name, 'component': f'Implicit{name}Style'} for name in ('Button', 'ComboBox', 'ComboBoxItem', 'CheckBox', 'Expander', 'TextBlock', 'TextBox', 'ToolTip', 'Window')],
        'archetypes': archetypes, 'warnings': [],
        'counts': {'tokens': len(tokens), 'colorTokens': sum(t['resourceType'] in ('Color', 'SolidColorBrush') for t in tokens), 'components': len(components), 'proposedColors': 0},
        'evidenceLimits': ['Source extraction only; WPF runtime, LiveCharts rendering, DPI and pixel layout require Windows.',
                           'Pinned npm extractor rejected this non-React repo; do not run uxproof sync to refresh WPF.',
                           'Existing screen examples document current structure, not approval of their visual design.'],
        'refreshCommand': 'python3 tools/ux-contract.py --refresh',
        'validationCommand': 'python3 tools/ux-contract.py --check',
    }
    return {'contract.json': contract, 'tokens.json': tokens, 'components.json': components}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument('--refresh', action='store_true')
    mode.add_argument('--check', action='store_true')
    args = parser.parse_args()
    data = extract()
    # Parse the complete app XAML tree and resolve house resource references.
    trees = [ET.parse(p) for p in sorted(APP.rglob('*.xaml'))]
    keys = {node.get(X + 'Key') for tree in trees for node in tree.iter() if node.get(X + 'Key')}
    resource_types = {node.get(X + 'Key'): node.tag.split('}')[-1]
                      for tree in trees for node in tree.iter() if node.get(X + 'Key')}
    import re
    for tree in trees:
        for node in tree.iter():
            dimension = {'ColumnDefinition': 'Width', 'RowDefinition': 'Height'}.get(node.tag.split('}')[-1])
            reference = re.fullmatch(r'\{(?:StaticResource|DynamicResource) ([A-Za-z][\w]*)\}', node.get(dimension, '') if dimension else '')
            if reference and resource_types.get(reference[1]) != 'GridLength':
                raise SystemExit(f'Grid dimension requires a GridLength resource: {reference[1]}')
            for value in node.attrib.values():
                for key in re.findall(r'\{(?:StaticResource|DynamicResource) ([A-Za-z][\w]*)\}', value):
                    if key not in keys:
                        raise SystemExit(f'Unresolved resource: {key}')
    light, dark = ({t['name'] for t in data['tokens.json'] if t['theme'] == theme} for theme in ('Light', 'Dark'))
    if light != dark:
        raise SystemExit('Light/dark resource keys differ')
    if args.check:
        for name, expected in data.items():
            if not (OUT / name).exists() or json.loads((OUT / name).read_text()) != expected:
                raise SystemExit(f'Contract is stale: {name}; inform the operator before --refresh')
        conventions = (OUT / 'conventions.md').read_text()
        if START not in conventions or END not in conventions:
            raise SystemExit('Missing manual-section markers')
    else:
        if (OUT / 'contract.json').exists() and not args.refresh:
            raise SystemExit('Contract exists; ask whether to refresh before using --refresh')
        OUT.mkdir(exist_ok=True)
        for name, content in data.items():
            (OUT / name).write_text(json.dumps(content, indent=2) + '\n')
        # Human conventions stay untouched: the manual section is never regenerated.
    print(f"WPF contract {'verified' if args.check else 'written'}: {data['contract.json']['counts']}; matching themes, valid XML and resource names.")


if __name__ == '__main__':
    main()
