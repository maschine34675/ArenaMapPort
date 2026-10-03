"""Resolve source art by verified identity rather than update-dependent object IDs."""
from common_ids import HardExtractError


def unique_named_texture(source_file, name, tag):
    """Require one exact Texture2D name in the selected serialized file.

    Path IDs are build-local: Arena 0.6 repacked resources.assets and reused the
    old banner IDs for unrelated textures. Never accept the old ID or choose the
    first duplicate. Callers additionally validate the expected dimensions.
    """
    matches = [obj for obj in source_file.objects.values()
               if obj.type.name == "Texture2D"
               and obj.read_typetree().get("m_Name") == name]
    if len(matches) != 1:
        raise HardExtractError(f"{tag}: expected one Texture2D named {name!r}, found {len(matches)}")
    return matches[0].read()
