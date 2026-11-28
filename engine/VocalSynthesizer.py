"""
VocalSynthesizer - vocal synthesizer

Read the .npy parameter files saved by VocalDecomposer and
rebuild the F0 / GlottalExcitation / breath objects to synthesize speech.

It does not include decomposition/pitch shifting; it only performs synthesis.
"""

import numpy as np
from typing import Optional, Union

import vocal_lib as vl


class VocalSynthesizer:
    """
    Vocal synthesizer.

    Read decomposition parameters from .npy files → rebuild F0 / GlottalExcitation / breath
    → call vl.synthesize_vocal() to synthesize speech.

    It does not include decomposition/pitch shifting; it only performs synthesis.
    """

    def __init__(self, sample_rate: int = 44100, model_type: str = 'lf'):
        """
        Args:
            sample_rate: sample rate (default 44100, will be overridden by the .npy file)
            model_type:  glottal model 'lf' | 'rosenberg'
        """
        self.sample_rate = sample_rate
        self.model_type = model_type
        # Glottal source time track (number of samples recorded after synthesizing the main signal, used as the breath trimming baseline)
        # External code can read vs.glottal_n_samples to get the glottal source length
        self.glottal_n_samples: Optional[int] = None

    # ------------------------------------------------------------------
    # 1. Load parameters from a directory
    # ------------------------------------------------------------------
    def load_params(self, dir_path: str) -> dict:
        """
        Read the parameter directory saved by VocalDecomposer (27 pure numeric files).

        Args:
            dir_path: parameter directory path

        Returns:
            params: parameter dict
        """
        import sys
        sys.path.insert(0, '.')
        from VocalDecomposer import VocalDecomposer
        params = VocalDecomposer.load(dir_path)
        self.sample_rate = int(params['sample_rate'])
        return params

    # ------------------------------------------------------------------
    # 2. Rebuild objects from the parameter dict
    # ------------------------------------------------------------------
    @staticmethod
    def rebuild_f0(params: dict) -> vl.F0:
        """Rebuild the F0 object from the parameter dict"""
        return vl.F0(
            values=params['f0_values'],
            time_axis=params['f0_time_axis'],
            frame_period=float(params['f0_frame_period']),
            sample_rate=int(params['sample_rate']),
            voiced_flags=params['f0_voiced_flags'],
        )

    @staticmethod
    def rebuild_glottal(params: dict) -> vl.GlottalExcitation:
        """Rebuild the GlottalExcitation object from the parameter dict"""
        ge_sp = params.get('ge_sp')
        ge_ap = params.get('ge_ap')
        if ge_sp is not None and len(ge_sp) == 0:
            ge_sp = None
        if ge_ap is not None and len(ge_ap) == 0:
            ge_ap = None

        return vl.GlottalExcitation(
            signal=params['ge_signal'],
            sample_rate=int(params['sample_rate']),
            sp=ge_sp,
            ap=ge_ap,
            oq=float(params['ge_oq']),
            glottal_intensity=params.get('ge_glottal_intensity'),
            parameters=dict(params.get('ge_parameters', {})),
        )

    @staticmethod
    def rebuild_breath(params: dict) -> vl.WAVAudio:
        """Rebuild the breath WAVAudio object from the parameter dict"""
        breath_data = params.get('breath_data')
        if breath_data is None or len(breath_data) == 0:
            return vl.WAVAudio(data=np.zeros(0), sample_rate=int(params['sample_rate']))
        return vl.WAVAudio(
            data=breath_data,
            sample_rate=int(params['sample_rate']),
        )

    # ------------------------------------------------------------------
    # 3. Synthesis
    # ------------------------------------------------------------------
    def synthesize(self,
                   params: Optional[dict] = None,
                   dir_path: Optional[str] = None,
                   f0: Optional[vl.F0] = None,
                   glottal: Optional[vl.GlottalExcitation] = None,
                   breath: Optional[vl.WAVAudio] = None,
                   match_rms_to: Optional[float] = None,
                   add_breath: bool = False,
                   breath_gain: float = 0.15,
                   ) -> vl.WAVAudio:
        """
        Vocal synthesis.

        Three input modes are supported (in decreasing priority):
            1. Pass f0 / glottal / breath objects directly
            2. Pass params (dict, from load_params)
            3. Pass dir_path (parameter directory path, load_params is called automatically)

        Args:
            params:         parameter dict (from load_params)
            dir_path:       parameter directory path (27 pure numeric files)
            f0:             optional, F0 object passed directly
            glottal:        optional, GlottalExcitation object passed directly
            breath:         optional, WAVAudio object passed directly (breath)
            match_rms_to:   target RMS (optional)
            add_breath:     whether to mix the breath bypass (default False)
            breath_gain:    independent gain of the breath bypass (default 0.15)

        Returns:
            the synthesized WAVAudio object
        """
        # Priority 3: dir_path
        if dir_path is not None:
            params = self.load_params(dir_path)

        # Priority 2: params dict
        if params is not None:
            f0 = self.rebuild_f0(params)
            glottal = self.rebuild_glottal(params)
            breath = self.rebuild_breath(params)

        if f0 is None or glottal is None:
            raise ValueError("f0 / glottal is required; pass it via params/dir_path or directly")

        # Synthesis
        main_audio = vl.synthesize_vocal(
            f0, breath, glottal,
            match_rms_to=match_rms_to,
            model_type=self.model_type,
        )

        # Record the glottal source time track (exposed externally)
        self.glottal_n_samples = len(main_audio.data)

        # ── Time axis diagnostics (after synthesis: main signal vs breath) ──
        if breath is not None:
            n_breath_in = len(breath.data)
            print("\n[Timeline Diagnostics - Post-synthesis]")
            print(f"  Synthesized main length = {self.glottal_n_samples} samples  ({self.glottal_n_samples/main_audio.sample_rate:.4f}s)")
            print(f"  Input breath length     = {n_breath_in} samples  ({n_breath_in/main_audio.sample_rate:.4f}s)")
            print(f"  Difference (main-breath)= {self.glottal_n_samples - n_breath_in} samples")
            print(f"  f0 frames               = {len(f0.values)}  (synthesis length = {int(len(f0.values) * f0.frame_period / 1000.0 * f0.sample_rate)})")
            print(f"  Lengths equal?          = {self.glottal_n_samples == n_breath_in}")

        # Breath mixing: handled on the same time axis as the main signal (already spliced and length-aligned externally with _xf),
        # so no separate peak normalization, trimming, or zero-padding is applied to the bypass
        if add_breath and breath is not None and len(breath.data) > 0:
            main_data = main_audio.data
            breath_data = breath.data
            n_main = self.glottal_n_samples
            n_breath = len(breath_data)

            # Strictly trim to the main signal length (only handles mismatched lengths, without changing the time axis)
            if n_breath >= n_main:
                breath_aligned = breath_data[:n_main]
            else:
                breath_aligned = np.zeros(n_main, dtype=breath_data.dtype)
                breath_aligned[:n_breath] = breath_data

            # Mix directly with a gain factor (no separate normalization)
            output_data = main_data + breath_aligned * breath_gain

            # Overall peak protection
            peak = np.max(np.abs(output_data))
            if peak > 0.99:
                output_data = output_data * (0.99 / peak)

            return vl.WAVAudio(data=output_data, sample_rate=main_audio.sample_rate)

        return main_audio

    # ------------------------------------------------------------------
    # 4. One-click pipeline: load parameter directory → synthesize → save
    # ------------------------------------------------------------------
    def run(self,
            dir_path: str,
            output_path: Optional[str] = None,
            match_rms_to: Optional[float] = None,
            add_breath: bool = False,
            breath_gain: float = 0.15,
            bit_depth: int = 24,
            ) -> vl.WAVAudio:
        """
        One-click pipeline: load parameter directory → synthesize → (optionally) save.

        Args:
            dir_path:     parameter directory path saved by VocalDecomposer
            output_path:  output WAV path (optional)
            match_rms_to: target RMS (optional)
            add_breath:   whether to mix the breath bypass
            breath_gain:  gain of the breath bypass
            bit_depth:    output bit depth 8/16/24

        Returns:
            the synthesized WAVAudio
        """
        # 1. Load parameters
        params = self.load_params(dir_path)
        sr = int(params['sample_rate'])
        n_frames = int(params['n_frames'])
        print(f"[Parameter Load] {dir_path}/")
        print(f"  F0: {n_frames} frames, sr={sr}, OQ={params['oq']:.4f}, "
              f"CQ={params['cq']:.4f}, QQ={params['qq']:.4f}")
        print(f"  jitter: {params['jitter_local']:.4f}%, "
              f"shimmer: {params['shimmer_local']:.4f}%")

        # 2. Synthesis
        output = self.synthesize(
            params=params,
            match_rms_to=match_rms_to,
            add_breath=add_breath,
            breath_gain=breath_gain,
        )
        print(f"[Synthesis Complete] duration {output.duration:.3f}s, RMS={output.rms:.4f}")

        # 3. Save
        if output_path:
            output.save(output_path, bit_depth=bit_depth)
            print(f"[Saved] {output_path}")

        return output


# ----------------------------------------------------------------------
# Usage example
# ----------------------------------------------------------------------
if __name__ == "__main__":
    import os
    os.makedirs("_output", exist_ok=True)

    # ===== Step 1: decompose and save with VocalDecomposer =====
    from VocalDecomposer import VocalDecomposer

    input_wav = sys.argv[1] if len(sys.argv) > 1 else os.environ.get("VOCALB1_DEMO_WAV", "")
    if not input_wav:
        raise SystemExit("usage: python VocalSynthesizer.py <input.wav>")
    param_dir = "_output/ei_params"

    if not os.path.isdir(param_dir):
        decomposer = VocalDecomposer(sample_rate=44100, frame_period=5.0)
        decomposer.decompose_and_save(input_wav, param_dir)
    else:
        print(f"[Skip Decomposition] {param_dir}/ already exists")

    # ===== Step 2: load and synthesize with VocalSynthesizer =====
    print("\n" + "=" * 60)
    print("VocalSynthesizer: load parameter directory -> synthesis")
    print("=" * 60)

    vs = VocalSynthesizer(model_type='lf')

    output = vs.run(
        dir_path=param_dir,
        output_path="_output/ei_synthesized_from_npy.wav",
        match_rms_to=0.15,
        add_breath=False,
        bit_depth=24,
    )