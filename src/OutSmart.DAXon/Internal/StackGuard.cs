////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Copyright (c) 2026 OutSmart
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace OutSmart.DAXon.Internal
{
    /// <summary>
    /// Proactive stack-overflow guard. On .NET Framework a real StackOverflowException is
    /// uncatchable and kills the process, so the Java behavior (catch StackOverflowError at
    /// the recursion sites and report SXLM0001) cannot be reproduced reactively. Instead the
    /// recursion entry points call Probe(), which raises the catchable RecursionDepthError
    /// while there is still guaranteed headroom to unwind — the ported catch sites then
    /// convert it to the same errors Java reports (SXLM0001 / XTDE3400).
    /// The remaining stack is measured directly (thread stack bounds are fixed for the
    /// thread's lifetime, so the low bound is cached per thread) because
    /// RuntimeHelpers.EnsureSufficientExecutionStack demands 512 KB on 64-bit Framework —
    /// half of a default 1 MB thread — which would reject recursion depths that in fact
    /// complete comfortably.
    /// </summary>
    internal static class StackGuard
    {
        // Headroom kept in reserve when RecursionDepthError is raised. The original x64 value was
        // 256KB, measured while the abort was still converted to XPathException inside the deep
        // engine stack. That conversion made ~185 decorating catches re-enter exception dispatch
        // during the unwind. RecursionDepthError is now deliberately foreign until the API boundary,
        // so that depth-proportional cascade no longer exists. Keeping its obsolete reserve made every
        // probe fail immediately on a 256KB D365 Batch thread, even at logical depth zero.
        //
        // Re-calibrated against every hostile recursion shape after the foreign-exception change:
        // 64KB still allowed a real StackOverflowException on a 256KB thread, while 96KB survived.
        // Keep the next 32KB tier as safety margin on Framework. .NET 10 has separate calibrated
        // tiers because tier-0 JIT frames are larger and DOTNET_TieredCompilation=0 changes the
        // frame shape again; neither value is a percentage of the host stack, and depth-proportional
        // error paths add their own extraMargin.
        private static readonly ulong Margin = GetCalibratedMargin();

        private static ulong GetCalibratedMargin()
        {
            if (!RuntimeInformation.FrameworkDescription.StartsWith(".NET 10", StringComparison.Ordinal))
            {
                return 128UL * 1024;
            }

            return string.Equals(Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"), "0", StringComparison.Ordinal)
                ? 96UL * 1024
                : 64UL * 1024;
        }

        [ThreadStatic]
        private static ulong stackLow;   // low bound of this thread's reserved stack region

        private static volatile bool noApi;   // GetCurrentThreadStackLimits needs Win8/Server2012+

        // QTDBG_SG=1 traces remaining-stack headroom to stderr (cached: an env lookup per probe
        // would allocate on the hot path).
        private static readonly bool Dbg = Environment.GetEnvironmentVariable("QTDBG_SG") != null;
        private static int dbgCount;

        [DllImport("kernel32.dll")]
        private static extern void GetCurrentThreadStackLimits(out UIntPtr lowLimit, out UIntPtr highLimit);

        /// <summary>Throws RecursionDepthError if the remaining stack is too small to safely
        /// descend another recursion level. Adapts to the executing thread's stack size.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]   // sits on every guarded recursion entry
        public static void Probe()
        {
            Probe(0);
        }

        /// <summary>
        /// As <see cref="Probe()"/>, plus caller-supplied headroom. For recursions whose ERROR path
        /// costs far more stack than the descent: on .NET Framework every level that catches and
        /// re-codes an exception re-enters exception dispatch from inside its catch, so the stack
        /// grows while unwinding instead of shrinking. A caller that pays that per level must
        /// reserve for it here - a fixed margin cannot, since the shortfall scales with depth.
        /// NoInlining is load-bearing: every caller is a recursion entry whose frame PERSISTS down
        /// the descent, and inlining moves the address-taken probe local into that frame — the
        /// few bytes per level break the depth-2000-on-1MB contract (sof_depth_fused_hof).
        /// This dedicated frame is transient: it pops before the level descends. The EH-carrying
        /// init stays split out in ProbeSlow so this body remains one TLS read + compare.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Probe(ulong extraMargin)
        {
            ulong low = stackLow;
            if (low == 0)
            {
                ProbeSlow(extraMargin);
                return;
            }

            unsafe
            {
                byte probe;
                ulong remaining = (ulong)&probe - low;
                if (Dbg && (++dbgCount & 255) == 0)
                {
                    Console.Error.WriteLine("[SG] remaining=" + remaining / 1024 + "KB");
                }

                if (remaining < Margin + extraMargin)
                {
                    if (Dbg)
                    {
                        Console.Error.WriteLine("[SG] THREW at remaining=" + remaining / 1024 + "KB");
                    }

                    throw new RecursionDepthError();
                }
            }
        }

        // Once-per-thread init plus the pre-Windows-8 route (noApi leaves stackLow at 0, so
        // those threads land here on every probe, as before). Holds the EH that must not sit
        // in the inlined hot body.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ProbeSlow(ulong extraMargin)
        {
            if (!noApi)
            {
                try
                {
                    GetCurrentThreadStackLimits(out UIntPtr lo, out _);
                    stackLow = (ulong)lo;
                }
                catch (EntryPointNotFoundException)
                {
                    noApi = true;
                }
            }

            if (noApi)
            {
                FallbackProbe();
                return;
            }

            Probe(extraMargin);
        }

        // Pre-Windows-8 fallback: the BCL probe (conservative — 512 KB on 64-bit Framework).
        private static void FallbackProbe()
        {
            try
            {
                RuntimeHelpers.EnsureSufficientExecutionStack();
            }
            catch (InsufficientExecutionStackException)
            {
                throw new RecursionDepthError();
            }
        }
    }
}
