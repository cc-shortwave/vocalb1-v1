namespace VocalB1.Core.Models;

/// <summary>
/// Global parameters for the synthesis pipeline (corresponding to config sections [2]~[8] in VocalB1.py).
/// Defaults match VocalB1.py so the C# side does not introduce a second set of default semantics.
/// Tempo-related parameters (bpm / timeSignature) belong to the project beat grid; see <see cref="ProjectDocument"/>.
/// </summary>
public sealed class SynthesisSettings
{
    // [2] Pitch transformation
    public double[] FollowRatio { get; set; } = [0.5, 0.45, 0.25, 0.15, 0.1, 0.05, 0.0];
    public double[] BandRatios { get; set; } = [1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0];
    public double BreathyThr { get; set; } = 0.3;
    public double BandWidthHz { get; set; } = 250.0;

    // [3] AP correction
    public double ApScale { get; set; } = 0.25;
    public double ApMax { get; set; } = 0.20;

    // [4] Concatenation / duration
    public int SampleRate { get; set; } = 44100;
    public double FramePeriod { get; set; } = 5.0;
    public bool JoinNormalizeSp { get; set; }
    public int DurEdgeFrames { get; set; } = 25;
    public int DurJitterSmooth { get; set; } = 5;
    public double DurJitterDecayMs { get; set; } = 120.0;
    public double DurTailJitterRatio { get; set; } = 0.3;

    // [5] Synthesis
    public double MatchRmsTo { get; set; } = 0.3;
    public string ModelType { get; set; } = "lf";
    public double SpFloor { get; set; } = 1e-12;

    // [6] Breath
    public bool AddBreath { get; set; } = true;
    public double BreathGain { get; set; } = 1.0;
    public bool BreathFollowPitch { get; set; } = true;
    public double BreathPvFrameMs { get; set; } = 60.0;
    public double BreathPvHopMs { get; set; } = 15.0;
    public bool BreathShapeToMain { get; set; } = true;
    public int BreathShapeBands { get; set; } = 48;
    public double BreathShapeGainMin { get; set; } = 0.03;
    public double BreathShapeGainMax { get; set; } = 30.0;

    // [7] Plosive
    public double PlosiveRatio { get; set; } = 5.0;
    public double PlosiveMinGradDb { get; set; } = 12.0;
    public double PlosiveMinCrest { get; set; } = 2.0;
    public double PlosiveMaxValley { get; set; } = 0.3;
    public double PlosiveGain { get; set; } = 0.3;
    public int PlosiveSmooth { get; set; } = 5;
    public int PlosiveMarginHead { get; set; }
    public int PlosiveMarginTail { get; set; } = 6;
    public bool PlosiveKeepBurst { get; set; } = true;
    public bool PlosiveEnvTransplant { get; set; } = true;
    public int PlosiveEnvHead { get; set; } = 6;
    public int PlosiveEnvTail { get; set; } = 6;
    public double PlosiveEnvGainMin { get; set; } = 0.15;
    public double PlosiveEnvGainMax { get; set; } = 2.0;

    // [8] Jitter/shimmer intensity (0~100, added on top of the voicebank's measured jitter/shimmer)
    public double Jitter { get; set; } = 0.0;
    public double Shimmer { get; set; } = 0.0;

    // [9] Output
    public double EdgeFadeMs { get; set; } = 12.0;
    public int BitDepth { get; set; } = 24;

    /// <summary>Stereo balance (0~100): the higher the value, the more energy in the right channel; 50 is centered.</summary>
    public double StereoBalance { get; set; } = 50.0;
    public bool CleanIntermediate { get; set; } = true;
    public bool Verbose { get; set; } = true;

    public SynthesisSettings Clone() => (SynthesisSettings)MemberwiseClone();
}
