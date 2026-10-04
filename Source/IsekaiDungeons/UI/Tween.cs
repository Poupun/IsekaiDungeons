using UnityEngine;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// Interpolation curves for the mod's UI animation.
    ///
    /// EVERY function here is a PURE function of elapsed time. That is not a
    /// stylistic preference, it is a correctness requirement: IMGUI runs
    /// OnGUI more than once per rendered frame (Layout and Repaint at minimum,
    /// plus one pass per input event), so anything that integrated state
    /// forward — the usual `velocity += force * dt` spring — would step two to
    /// five times per frame and run at a speed that depended on how much the
    /// player was moving the mouse. Solving the spring analytically instead
    /// makes the animation identical no matter how many times it is asked to
    /// draw itself, and identical on a 30fps laptop and a 240fps desktop.
    ///
    /// All times are in real seconds (see <see cref="Now"/>), never game ticks
    /// — a title that plays six times faster because the player is on
    /// fast-forward is a bug.
    /// </summary>
    public static class Tween
    {
        /// <summary>Below this residual a decaying spring is treated as arrived.</summary>
        private const float SettleEpsilon = 0.0005f;

        /// <summary>
        /// THE ANIMATION CLOCK. Every animated thing in this mod reads time
        /// from here and from nowhere else.
        ///
        /// It is NOT Time.realtimeSinceStartup, and the difference is the whole
        /// reason this property exists. That property is a live wall-clock read
        /// that returns a different answer every time it is called, including
        /// twice within one rendered frame. RimWorld's frame runs in this
        /// order:
        ///
        ///     RealTime.Update()             snapshot the clock, once
        ///     TickManagerUpdate()           run 1..N game ticks
        ///     map.MapUpdate() -> DrawAt()   we draw here
        ///
        /// so a live read at draw time returns "frame start + however long the
        /// tick loop just took". That is not a constant: it grows with game
        /// speed and colony size, and TickManager explicitly lets the loop burn
        /// up to 45.45ms in a single frame before bailing out. A 24fps sequence
        /// has 41.7ms between frames, so on a busy colony the sampling error
        /// can exceed a whole animation frame — the sequence stutters, repeats
        /// and skips while the game's own framerate never drops. "The game is
        /// not lagging but the animation is" is the exact signature of this,
        /// and it gets worse the more ticks there are per frame.
        ///
        /// RealTime.LastRealTime is the same clock snapshotted by RimWorld
        /// before the tick loop and held constant for the rest of the frame. It
        /// also makes IMGUI correct for free: OnGUI runs several times per
        /// frame (Layout, Repaint, one pass per input event) and a live read
        /// would hand each pass a different time.
        ///
        /// Falls back to the live read only when RealTime has never ticked —
        /// the main menu before the first frame, where 0 would mean "the
        /// animation started at the dawn of the process".
        /// </summary>
        public static float Now
        {
            get
            {
                float t = RealTime.LastRealTime;
                return t > 0f ? t : Time.realtimeSinceStartup;
            }
        }

        // ── Easing ──────────────────────────────────────────────────────────

        public static float EaseOutCubic(float t)
        {
            t = Mathf.Clamp01(t);
            float u = 1f - t;
            return 1f - u * u * u;
        }

        public static float EaseOutQuint(float t)
        {
            t = Mathf.Clamp01(t);
            float u = 1f - t;
            return 1f - u * u * u * u * u;
        }

        public static float EaseInCubic(float t)
        {
            t = Mathf.Clamp01(t);
            return t * t * t;
        }

        public static float EaseInOutCubic(float t)
        {
            t = Mathf.Clamp01(t);
            return t < 0.5f
                ? 4f * t * t * t
                : 1f - Mathf.Pow(-2f * t + 2f, 3f) * 0.5f;
        }

        /// <summary>Overshoots past 1 and comes back. The classic "pop".</summary>
        public static float EaseOutBack(float t, float overshoot = 1.70158f)
        {
            t = Mathf.Clamp01(t);
            float u = t - 1f;
            return 1f + (overshoot + 1f) * u * u * u + overshoot * u * u;
        }

        // ── Spring ──────────────────────────────────────────────────────────

        /// <summary>
        /// Unit step response of a damped harmonic oscillator, solved in closed
        /// form: 0 at t=0, settling on 1, overshooting on the way if zeta &lt; 1.
        ///
        /// <paramref name="omega"/> is the undamped angular frequency — bigger
        /// is snappier. <paramref name="zeta"/> is the damping ratio: 1 is
        /// critically damped (fastest arrival with no overshoot), below 1
        /// bounces, and around 0.4–0.6 gives the springy overshoot that reads
        /// as "alive" without looking like a bug.
        /// </summary>
        public static float Spring01(float t, float omega = 14f, float zeta = 0.45f)
        {
            if (t <= 0f)
                return 0f;

            omega = Mathf.Max(0.01f, omega);
            zeta = Mathf.Max(0.01f, zeta);

            if (zeta < 1f)
            {
                float damped = omega * Mathf.Sqrt(1f - zeta * zeta);
                float decay = Mathf.Exp(-zeta * omega * t);
                if (decay < SettleEpsilon)
                    return 1f;
                return 1f - decay * (Mathf.Cos(damped * t) + zeta * omega / damped * Mathf.Sin(damped * t));
            }

            // Critically damped (and, close enough for UI, over-damped).
            float e = Mathf.Exp(-omega * t);
            if (e < SettleEpsilon)
                return 1f;
            return 1f - e * (1f + omega * t);
        }

        /// <summary>Spring interpolation between two values.</summary>
        public static float Spring(float t, float from, float to, float omega = 14f, float zeta = 0.45f)
        {
            return from + (to - from) * Spring01(t, omega, zeta);
        }

        /// <summary>
        /// How long the given spring needs before it is visually at rest.
        /// Used to work out when an intro is over instead of hard-coding a
        /// duration that stops matching the moment the tuning changes.
        /// </summary>
        public static float SettleTime(float omega = 14f, float zeta = 0.45f)
        {
            omega = Mathf.Max(0.01f, omega);
            zeta = Mathf.Clamp(zeta, 0.01f, 1f);
            // exp(-zeta*omega*t) == SettleEpsilon  =>  t == -ln(eps) / (zeta*omega)
            return -Mathf.Log(SettleEpsilon) / (zeta * omega);
        }

        // ── Staggering ──────────────────────────────────────────────────────

        /// <summary>
        /// Local elapsed time for item <paramref name="index"/> of a staggered
        /// run. Negative means "has not started yet", which every curve here
        /// already treats as 0.
        /// </summary>
        public static float LocalTime(float t, float delay, float stagger, int index)
        {
            return t - delay - stagger * index;
        }

        /// <summary>A slow symmetric pulse, 0 → 1 → 0, for idle twinkles.</summary>
        public static float Pulse(float t, float period)
        {
            if (period <= 0f)
                return 0f;
            return 0.5f - 0.5f * Mathf.Cos(t / period * Mathf.PI * 2f);
        }
    }
}
