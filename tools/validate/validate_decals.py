"""Compare the actual bundled native components against their source-backed package.

Includes exact hierarchy/local transforms and original active/enabled state, material
identity, normalized atlas region, queue, tint, and all projection/fade settings.
"""
import json
from collections import Counter
from pathlib import Path
from UnityPy.classes import PPtr


def rounded(values):
    return tuple(round(float(v), 4) for v in values)


def chain_key(chain):
    return tuple((name, rounded(pos), rounded(rot), rounded(scale)) for name, pos, rot, scale in chain)


def signature(chain, active, enabled, material, lower, upper, size, queue, clipping, tint):
    return (chain_key(chain), bool(active), bool(enabled), material,
            rounded([lower[0]/size[0], lower[1]/size[1], upper[0]/size[0], upper[1]/size[1]]),
            queue, rounded(clipping), rounded(tint))


def bundle_errors(env, package, manifest, files, package_chain, bundle_owner):
    summary = manifest.get('native_features', {}).get('static_decals')
    if summary is None:
        return []
    package = Path(package)
    by_name = {sf.name: sf for sf in files}
    errors = []
    total = 0
    for name in manifest['scenes']:
        source = json.loads((package / 'scenes' / (name+'.json')).read_text(encoding='utf-8'))
        owners = {o['id']:o for o in source['objects']}
        wanted = Counter()
        for owner in owners.values():
            for c in owner['components']:
                if c['type'] != 'StaticDeferredDecal': continue
                wanted[signature(package_chain(owner,owners),owner['active'],c['enabled'],c['material_id'],
                                 c['lower_left'],c['upper_right'],c['texture_size'],c['queue'],
                                 [c['normal_clip'],c['normal_fade'],c['edge_clip'],c['edge_fade']],c['tint'])] += 1
        sf = by_name.get('BuildPlayer-'+name)
        if sf is None:
            errors.append(name+': no built scene'); continue
        found = Counter()
        cache = {}
        for reader in sf.objects.values():
            if reader.type.name != 'MonoBehaviour': continue
            tree = reader.read_typetree()
            def resolve(ptr):
                return PPtr(m_FileID=ptr['m_FileID'], m_PathID=ptr['m_PathID'], assetsfile=reader.assets_file).deref()
            script = resolve(tree['m_Script']).read_typetree()
            if script['m_ClassName'] != 'StaticDeferredDecal': continue
            if script['m_Namespace'] or script['m_AssemblyName'].removesuffix('.dll') != 'Assembly-CSharp':
                errors.append(name+': invalid native script identity')
            owner = bundle_owner(reader,cache)
            mat_reader = resolve(tree['_material'])
            mat = mat_reader.read()
            tex = dict(mat.m_SavedProperties.m_TexEnvs)['_MainTex'].m_Texture.deref().read()
            vec = lambda field: [tree[field]['x'],tree[field]['y']]
            found[signature(owner['chain'],owner['active'],tree['m_Enabled'],mat.m_Name,
                            vec('LowerLeftPixel'),vec('UpperRightPixel'),[tex.m_Width,tex.m_Height],tree['_decalQueue'],
                            [tree['_normalClip'],tree['_normalAlphaFadeoff'],tree['_edgeClip'],tree['_edgeAlphaFadeoff']],
                            [tree['_tint'][k] for k in 'rgba'])] += 1
        if found != wanted:
            missing, extra = wanted-found, found-wanted
            errors.append(f'{name}: decal fields differ: expected={sum(wanted.values())} actual={sum(found.values())} '
                          f'missing={sum(missing.values())} extra={sum(extra.values())}; '
                          f'first missing={next(iter(missing),None)}; first extra={next(iter(extra),None)}')
        total += sum(found.values())
    if total != summary['count']:
        errors.append(f'bundle decal census {total} != package {summary["count"]}')
    return errors
