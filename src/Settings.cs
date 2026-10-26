using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MotionCues
{
    public enum Sensitivity { Low, Medium, High }

    /// <summary>
    /// User settings persisted to %APPDATA%\MotionCues\settings.json.
    /// Unknown/missing fields fall back to the defaults below, so the file can be hand-edited.
    /// </summary>
    public sealed class Settings
    {
        // ---- user-facing (tray menu) ----
        public bool ShowDots { get; set; } = true;
        /// <summary>Flip the lateral (left/right) dot direction.</summary>
        public bool InvertX { get; set; } = false;
        /// <summary>Flip the longitudinal (accelerate/brake -> down/up) dot direction.</summary>
        public bool InvertZ { get; set; } = false;
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public Sensitivity Sensitivity { get; set; } = Sensitivity.Medium;
        public bool ShowDebug { get; set; } = false;
        public bool AutoMode { get; set; } = false;

        // ---- motion (Settings window) ----
        /// <summary>Flow mode: a steady turn scrolls the dots sideways at a steady speed instead of
        /// pushing them to a fixed offset. Short jolts barely move them.</summary>
        public bool FlowMode { get; set; } = true;
        /// <summary>Flow mode: horizontal scroll speed, DIP per second per g.</summary>
        public double FlowSpeedX { get; set; } = 500;
        /// <summary>Offset mode: horizontal offset, DIP per g.</summary>
        public double TurnGain { get; set; } = 750;
        /// <summary>Vertical (accelerate/brake) offset, DIP per g.</summary>
        public double VerticalGain { get; set; } = 300;
        /// <summary>Spring stiffness of the on-screen easing (higher = dots catch up faster).</summary>
        public double SpringStiffness { get; set; } = 30;
        /// <summary>1 = critically damped (no overshoot); below 1 the dots bounce a little before settling.</summary>
        public double SpringDampingRatio { get; set; } = 1.0;

        // ---- appearance ----
        public int DotsPerSide { get; set; } = 7;
        public double DotDiameter { get; set; } = 14;
        /// <summary>Distance from screen edge to the column centre, DIP.</summary>
        public double EdgeMargin { get; set; } = 70;
        /// <summary>Width of the horizontal band a column may wrap within, DIP.</summary>
        public double BandWidth { get; set; } = 140;
        /// <summary>Below 1 the inverse-colour dots blend with the screen and drift toward grey.</summary>
        public double DotOpacity { get; set; } = 1.0;

        // ---- particle fountain ----
        /// <summary>During steady motion, extra dots of random size stream the way the cue dots move.</summary>
        public bool FountainEnabled { get; set; } = true;
        /// <summary>Particles spawned per second per side at full strength.</summary>
        public double FountainAmount { get; set; } = 40;
        /// <summary>Average particle speed, DIP per second.</summary>
        public double FountainSpeed { get; set; } = 220;
        /// <summary>Steady motion (g, after sensitivity) needed before particles start.</summary>
        public double FountainStartG { get; set; } = 0.04;
        /// <summary>How far a particle may veer from the stream direction, degrees either side.</summary>
        public double FountainSpreadDegrees { get; set; } = 25;
        /// <summary>Width of the area around each column where particles live, DIP.</summary>
        public double FountainWidth { get; set; } = 260;
        public double ParticleSizeMin { get; set; } = 4;
        public double ParticleSizeMax { get; set; } = 12;

        // ---- dim mode ----
        /// <summary>Darken the display output itself (gamma table, then colour matrix), past the lowest backlight.</summary>
        public bool DimMode { get; set; } = false;
        /// <summary>How much less light the screen gives off: 0 = no change, 0.9 = 90% less.</summary>
        public double DimAmount { get; set; } = 0.5;

        // ---- signal processing ----
        public int ReportIntervalMs { get; set; } = 16;
        public double GravityTauSeconds { get; set; } = 5;
        public double SmoothTauSeconds { get; set; } = 0.2;
        public double DeadzoneG { get; set; } = 0.02;
        public double ClampG { get; set; } = 0.4;
        /// <summary>Remove the vertical component (lid tilt) before mapping Z to forward/back.</summary>
        public bool TiltCompensation { get; set; } = true;
        /// <summary>Divide by |gravity| so a sensor reading e.g. 1.25 g at rest still yields true g.</summary>
        public bool NormalizeByGravity { get; set; } = true;

        public double AutoVarianceThreshold { get; set; } = 0.0009; // g^2
        public double AutoWindowSeconds { get; set; } = 5;
        public double AutoCalmSeconds { get; set; } = 30;

        /// <summary>Tray Low / Medium / High multiplier applied on top of every gain.</summary>
        [JsonIgnore]
        public double SensitivityScale => Sensitivity switch
        {
            Sensitivity.Low => 0.6,
            Sensitivity.High => 1.5,
            _ => 1.0,
        };

        // ---------------------------------------------------------------------------------

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        public static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MotionCues", "settings.json");

        public static Settings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), JsonOpts);
                    if (s != null) { s.Sanitize(); return s; }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Settings load failed: " + ex.Message);
            }
            return new Settings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Settings save failed: " + ex.Message);
            }
        }

        /// <summary>Keep hand-edited values in a range that can't break the app.</summary>
        public void Sanitize()
        {
            DotsPerSide = Math.Clamp(DotsPerSide, 1, 30);
            DotDiameter = Math.Clamp(DotDiameter, 4, 64);
            BandWidth = Math.Clamp(BandWidth, DotDiameter * 2, 600);
            EdgeMargin = Math.Clamp(EdgeMargin, 0, 600);
            DotOpacity = Math.Clamp(DotOpacity, 0.05, 1);
            FountainAmount = Math.Clamp(FountainAmount, 0, 300);
            FountainSpeed = Math.Clamp(FountainSpeed, 0, 2000);
            FountainStartG = Math.Clamp(FountainStartG, 0, 1);
            FountainSpreadDegrees = Math.Clamp(FountainSpreadDegrees, 0, 180);
            FountainWidth = Math.Clamp(FountainWidth, 20, 1000);
            ParticleSizeMin = Math.Clamp(ParticleSizeMin, 1, 64);
            ParticleSizeMax = Math.Clamp(ParticleSizeMax, ParticleSizeMin, 64);
            DimAmount = Math.Clamp(DimAmount, 0, 0.95);
            ReportIntervalMs = Math.Clamp(ReportIntervalMs, 1, 1000);
            GravityTauSeconds = Math.Clamp(GravityTauSeconds, 0.2, 30);
            SmoothTauSeconds = Math.Clamp(SmoothTauSeconds, 0.005, 2);
            ClampG = Math.Clamp(ClampG, 0.05, 2);
            SpringStiffness = Math.Clamp(SpringStiffness, 1, 1000);
            SpringDampingRatio = Math.Clamp(SpringDampingRatio, 0.1, 3);
            FlowSpeedX = Math.Clamp(FlowSpeedX, 0, 5000);
            TurnGain = Math.Clamp(TurnGain, 0, 5000);
            VerticalGain = Math.Clamp(VerticalGain, 0, 5000);
        }

        /// <summary>Copy every persisted value from another instance (used by "Reset to defaults").</summary>
        public void CopyFrom(Settings other)
        {
            foreach (var prop in typeof(Settings).GetProperties())
                if (prop.CanRead && prop.CanWrite) prop.SetValue(this, prop.GetValue(other));
        }

        public void ApplyTo(MotionProcessor p)
        {
            p.GravityTau = GravityTauSeconds;
            p.SmoothTau = SmoothTauSeconds;
            p.DeadzoneG = DeadzoneG;
            p.ClampG = ClampG;
            p.TiltCompensation = TiltCompensation;
            p.NormalizeByGravity = NormalizeByGravity;
            p.AutoWindowSeconds = AutoWindowSeconds;
            p.AutoVarianceThreshold = AutoVarianceThreshold;
        }
    }
}
