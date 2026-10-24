"""
vocal_lib - vocal analysis and synthesis library

Object-oriented vocal processing library providing:
- Data classes: F0, WAVAudio, GlottalExcitation
- Decomposition: decompose_vocal (WAV -> F0 + breath WAV + GlottalExcitation)
- Synthesis: synthesize_vocal (F0 + breath WAV + GlottalExcitation -> WAV)
- Pitch shifting: pitch_shift (F0 + GlottalExcitation + source/target pitch -> new F0 + new GlottalExcitation)
"""

from .data_classes import F0, WAVAudio, GlottalExcitation
from .decomposition import decompose_vocal
from .synthesis import synthesize_vocal
from .pitch import pitch_shift

__all__ = [
    'F0',
    'WAVAudio',
    'GlottalExcitation',
    'decompose_vocal',
    'synthesize_vocal',
    'pitch_shift',
]

__version__ = '1.1.0'
__author__ = 'vocalB1'
__license__ = 'MIT'
