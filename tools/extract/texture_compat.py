"""Scoped compatibility fixes for formats reached by the generic map extractor."""
from contextlib import contextmanager


@contextmanager
def bc6h_rgb_decoder():
    """Pillow's BC6H decoder requires RGB; UnityPy 1.25.2 requests RGBA.

    Keep UnityPy's existing base-mip decoding, RGB conversion and vertical
    convention. Restore its table on every exit; legacy extractor entrypoints
    and other texture formats are unaffected. This retains the spike's 8-bit
    texture export, not the source HDR radiance range.
    """
    from UnityPy.enums import TextureFormat
    from UnityPy.export import Texture2DConverter as converter

    fmt = TextureFormat.BC6H
    previous = converter.CONV_TABLE[fmt]
    affected = previous == (converter.pillow, ('RGBA', 'bcn', 6))
    if affected:
        converter.CONV_TABLE[fmt] = (converter.pillow, ('RGB', 'bcn', 6))
    try:
        yield
    finally:
        if affected:
            converter.CONV_TABLE[fmt] = previous
