// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Text;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Build.Shared;

namespace Microsoft.Build
{
    /// <summary>
    /// This class is used to selectively intern strings. It should be used at the point of new string creation.
    /// For example,
    ///
    ///     string interned = OpportunisticIntern.Intern(String.Join(",",someStrings));
    ///
    /// There are currently two underlying implementations. The new default one in WeakStringCacheInterner is based on weak GC handles.
    /// The legacy one in BucketedPrioritizedStringList is available only as an escape hatch by setting an environment variable.
    ///
    /// The legacy implementation uses heuristics to decide whether it will be efficient to intern a string or not. There is no
    /// guarantee that a string will intern.
    ///
    /// The thresholds and sizes were determined by experimentation to give the best number of bytes saved
    /// at reasonable elapsed time cost.
    ///
    /// The new implementation interns all strings but maintains only weak references so it doesn't keep the strings alive.
    /// </summary>
    internal sealed class OpportunisticIntern
    {
        /// <summary>
        /// Defines the interner interface as we currently implement more than one.
        /// </summary>
        private interface IInternerImplementation
        {
            /// <summary>
            /// Converts the given internable candidate to its string representation. Efficient implementions have side-effects
            /// of caching the results to end up with as few duplicates on the managed heap as practical.
            /// </summary>
            string InterningToString<T>(T candidate) where T : IInternable;

            /// <summary>
            /// Prints implementation specific interning statistics to the console.
            /// </summary>
            /// <param name="heading">A string identifying the interner in the output.</param>
            void ReportStatistics(string heading);
        }

        /// <summary>
        /// The singleton instance of OpportunisticIntern.
        /// </summary>
        internal static OpportunisticIntern Instance { get; private set; } = new OpportunisticIntern();

        /// <summary>
        /// The interner implementation in use.
        /// </summary>
        private IInternerImplementation _interner;

        private OpportunisticIntern()
        {
            _interner = new WeakStringCacheInterner(gatherStatistics: false);
        }

        /// <summary>
        /// Recreates the singleton instance based on the current environment (test only).
        /// </summary>
        internal static void ResetForTests()
        {
            Debug.Assert(BuildEnvironmentHelper.Instance.RunningTests);
            Instance = new OpportunisticIntern();
        }

        /// <summary>
        /// Turn on statistics gathering.
        /// </summary>
        internal void EnableStatisticsGathering()
        {
            _interner = new WeakStringCacheInterner(gatherStatistics: true);
        }

        /// <summary>
        /// Intern the given internable.
        /// </summary>
        internal static string InternableToString<T>(T candidate) where T : IInternable
        {
            return Instance.InternableToStringImpl(candidate);
        }

        /// <summary>
        /// Potentially Intern the given string builder.
        /// </summary>
        internal static string StringBuilderToString(StringBuilder candidate)
        {
            return Instance.InternableToStringImpl(new StringBuilderInternTarget(candidate));
        }

        /// <summary>
        /// Potentially Intern the given char array.
        /// </summary>
        internal static string CharArrayToString(char[] candidate, int count)
        {
            return Instance.InternableToStringImpl(new CharArrayInternTarget(candidate, count));
        }

        /// <summary>
        /// Potentially Intern the given char array.
        /// </summary>
        internal static string CharArrayToString(char[] candidate, int startIndex, int count)
        {
            return Instance.InternableToStringImpl(new CharArrayInternTarget(candidate, startIndex, count));
        }

        /// <summary>
        /// Potentially Intern the given string.
        /// </summary>
        /// <param name="candidate">The string to intern.</param>
        /// <returns>The interned string, or the same string if it could not be interned.</returns>
        internal static string InternStringIfPossible(string candidate)
        {
            return Instance.InternableToStringImpl(new StringInternTarget(candidate));
        }

        /// <summary>
        /// Intern the given internable.
        /// </summary>
        private string InternableToStringImpl<T>(T candidate) where T : IInternable
        {
            if (candidate.Length == 0)
            {
                // As in the case that a property or itemlist has evaluated to empty.
                return string.Empty;
            }

            string result = _interner.InterningToString(candidate);
#if DEBUG
            string expected = candidate.ExpensiveConvertToString();
            if (!String.Equals(result, expected))
            {
                ErrorUtilities.ThrowInternalError("Interned string {0} should have been {1}", result, expected);
            }
#endif
            return result;
        }

        /// <summary>
        /// Report statistics about interning. Don't call unless GatherStatistics has been called beforehand.
        /// </summary>
        internal void ReportStatistics()
        {
            _interner.ReportStatistics("Main");
        }

        private static bool TryInternHardcodedString<T>(T candidate, string str, ref string interned) where T : IInternable
        {
            Debug.Assert(candidate.Length == str.Length);

            if (candidate.StartsWithStringByOrdinalComparison(str))
            {
                interned = str;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Try to match the candidate with small number of hardcoded interned string literals.
        /// The return value indicates how the string was interned (if at all).
        /// </summary>
        /// <returns>
        /// True if the candidate matched a hardcoded literal, null if it matched a "do not intern" string, false otherwise.
        /// </returns>
        private static bool? TryMatchHardcodedStrings<T>(T candidate, out string interned) where T : IInternable
        {
            int length = candidate.Length;
            interned = null;

            // Each of the hard-coded small strings below showed up in a profile run with considerable duplication in memory.
            if (length == 2)
            {
                if (candidate[1] == '#')
                {
                    if (candidate[0] == 'C')
                    {
                        interned = "C#";
                        return true;
                    }

                    if (candidate[0] == 'F')
                    {
                        interned = "F#";
                        return true;
                    }
                }

                if (candidate[0] == 'V' && candidate[1] == 'B')
                {
                    interned = "VB";
                    return true;
                }
            }
            else if (length == 4)
            {
                if (TryInternHardcodedString(candidate, "TRUE", ref interned) ||
                    TryInternHardcodedString(candidate, "True", ref interned) ||
                    TryInternHardcodedString(candidate, "Copy", ref interned) ||
                    TryInternHardcodedString(candidate, "true", ref interned) ||
                    TryInternHardcodedString(candidate, "v4.0", ref interned))
                {
                    return true;
                }
            }
            else if (length == 5)
            {
                if (TryInternHardcodedString(candidate, "FALSE", ref interned) ||
                    TryInternHardcodedString(candidate, "false", ref interned) ||
                    TryInternHardcodedString(candidate, "Debug", ref interned) ||
                    TryInternHardcodedString(candidate, "Build", ref interned) ||
                    TryInternHardcodedString(candidate, "Win32", ref interned))
                {
                    return true;
                }
            }
            else if (length == 6)
            {
                if (TryInternHardcodedString(candidate, "''!=''", ref interned) ||
                    TryInternHardcodedString(candidate, "AnyCPU", ref interned))
                {
                    return true;
                }
            }
            else if (length == 7)
            {
                if (TryInternHardcodedString(candidate, "Library", ref interned) ||
                    TryInternHardcodedString(candidate, "MSBuild", ref interned) ||
                    TryInternHardcodedString(candidate, "Release", ref interned))
                {
                    return true;
                }
            }
            // see Microsoft.Build.BackEnd.BuildRequestConfiguration.CreateUniqueGlobalProperty
            else if (length > MSBuildConstants.MSBuildDummyGlobalPropertyHeader.Length &&
                    candidate.StartsWithStringByOrdinalComparison(MSBuildConstants.MSBuildDummyGlobalPropertyHeader))
            {
                // don't want to leak unique strings into the cache
                interned = candidate.ExpensiveConvertToString();
                return null;
            }
            else if (length == 24)
            {
                if (TryInternHardcodedString(candidate, "ResolveAssemblyReference", ref interned))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Implements interning based on a WeakStringCache (new implementation).
        /// </summary>
        private class WeakStringCacheInterner : IInternerImplementation
        {
            /// <summary>
            /// Enumerates the possible interning results.
            /// </summary>
            private enum InternResult
            {
                MatchedHardcodedString,
                FoundInWeakStringCache,
                AddedToWeakStringCache,
                RejectedFromInterning
            }

            /// <summary>
            /// The cache to keep strings in.
            /// </summary>
            private readonly WeakStringCache _weakStringCache = new WeakStringCache();

#region Statistics
            /// <summary>
            /// Whether or not to gather statistics.
            /// </summary>
            private readonly bool _gatherStatistics;

            /// <summary>
            /// Number of times interning with hardcoded string literals worked.
            /// </summary>
            private int _hardcodedInternHits;

            /// <summary>
            /// Number of times the regular interning path found the string in the cache.
            /// </summary>
            private int _regularInternHits;

            /// <summary>
            /// Number of times the regular interning path added the string to the cache.
            /// </summary>
            private int _regularInternMisses;

            /// <summary>
            /// Number of times interning wasn't attempted.
            /// </summary>
            private int _rejectedStrings;

            /// <summary>
            /// Total number of strings eliminated by interning.
            /// </summary>
            private int _internEliminatedStrings;

            /// <summary>
            /// Total number of chars eliminated across all strings.
            /// </summary>
            private int _internEliminatedChars;

            /// <summary>
            /// Maps strings that went though the regular (i.e. not hardcoded) interning path to the number of times they have been
            /// seen. The higher the number the better the payoff if the string had been hardcoded.
            /// </summary>
            private Dictionary<string, int> _missedHardcodedStrings;

#endregion

            public WeakStringCacheInterner(bool gatherStatistics)
            {
                if (gatherStatistics)
                {
                    _missedHardcodedStrings = new Dictionary<string, int>();
                }
                _gatherStatistics = gatherStatistics;
            }

            /// <summary>
            /// Intern the given internable.
            /// </summary>
            public string InterningToString<T>(T candidate) where T : IInternable
            {
                if (_gatherStatistics)
                {
                    return InternWithStatistics(candidate);
                }
                else
                {
                    TryIntern(candidate, out string result);
                    return result;
                }
            }

            /// <summary>
            /// Report statistics to the console.
            /// </summary>
            public void ReportStatistics(string heading)
            {
                string title = "Opportunistic Intern (" + heading + ")";
                Console.WriteLine("\n{0}{1}{0}", new string('=', 41 - (title.Length / 2)), title);
                Console.WriteLine("||{0,50}|{1,20:N0}|{2,8}|", "Hardcoded Hits", _hardcodedInternHits, "hits");
                Console.WriteLine("||{0,50}|{1,20:N0}|{2,8}|", "Hardcoded Rejects", _rejectedStrings, "rejects");
                Console.WriteLine("||{0,50}|{1,20:N0}|{2,8}|", "WeakStringCache Hits", _regularInternHits, "hits");
                Console.WriteLine("||{0,50}|{1,20:N0}|{2,8}|", "WeakStringCache Misses", _regularInternMisses, "misses");
                Console.WriteLine("||{0,50}|{1,20:N0}|{2,8}|", "Eliminated Strings*", _internEliminatedStrings, "strings");
                Console.WriteLine("||{0,50}|{1,20:N0}|{2,8}|", "Eliminated Chars", _internEliminatedChars, "chars");
                Console.WriteLine("||{0,50}|{1,20:N0}|{2,8}|", "Estimated Eliminated Bytes", _internEliminatedChars * 2, "bytes");
                Console.WriteLine("Elimination assumes that strings provided were unique objects.");
                Console.WriteLine("|---------------------------------------------------------------------------------|");

                IEnumerable<string> topMissingHardcodedString =
                    _missedHardcodedStrings
                    .OrderByDescending(kv => kv.Value * kv.Key.Length)
                    .Take(15)
                    .Where(kv => kv.Value > 1)
                    .Select(kv => string.Format(CultureInfo.InvariantCulture, "({1} instances x each {2} chars)\n{0}", kv.Key, kv.Value, kv.Key.Length));

                Console.WriteLine("##########Top Missing Hardcoded Strings:  \n{0} ", string.Join("\n==============\n", topMissingHardcodedString.ToArray()));
                Console.WriteLine();

                WeakStringCache.DebugInfo debugInfo = _weakStringCache.GetDebugInfo();
                Console.WriteLine("WeakStringCache statistics:");
                Console.WriteLine("String count live/collected/total = {0}/{1}/{2}", debugInfo.LiveStringCount, debugInfo.CollectedStringCount, debugInfo.LiveStringCount + debugInfo.CollectedStringCount);
            }

            /// <summary>
            /// Try to intern the string.
            /// The return value indicates the how the string was interned (if at all).
            /// </summary>
            private InternResult TryIntern<T>(T candidate, out string interned) where T : IInternable
            {
                // First, try the hard coded intern strings.
                bool? hardcodedMatchResult = TryMatchHardcodedStrings(candidate, out interned);
                if (hardcodedMatchResult != false)
                {
                    // Either matched a hardcoded string or is explicitly not to be interned.
                    return hardcodedMatchResult.HasValue ? InternResult.MatchedHardcodedString : InternResult.RejectedFromInterning;
                }

                interned = _weakStringCache.GetOrCreateEntry(candidate, out bool cacheHit);
                return cacheHit ? InternResult.FoundInWeakStringCache : InternResult.AddedToWeakStringCache;
            }

            /// <summary>
            /// Version of Intern that gathers statistics
            /// </summary>
            private string InternWithStatistics<T>(T candidate) where T : IInternable
            {
                lock (_missedHardcodedStrings)
                {
                    InternResult internResult = TryIntern(candidate, out string result);

                    switch (internResult)
                    {
                        case InternResult.MatchedHardcodedString:
                            _hardcodedInternHits++;
                            break;
                        case InternResult.FoundInWeakStringCache:
                            _regularInternHits++;
                            break;
                        case InternResult.AddedToWeakStringCache:
                            _regularInternMisses++;
                            break;
                        case InternResult.RejectedFromInterning:
                            _rejectedStrings++;
                            break;
                    }

                    if (internResult != InternResult.MatchedHardcodedString && internResult != InternResult.RejectedFromInterning)
                    {
                        _missedHardcodedStrings.TryGetValue(result, out int priorCount);
                        _missedHardcodedStrings[result] = priorCount + 1;
                    }

                    if (!candidate.ReferenceEquals(result))
                    {
                        // Reference changed so 'candidate' is now released and should save memory.
                        _internEliminatedStrings++;
                        _internEliminatedChars += candidate.Length;
                    }

                    return result;
                }
            }
        }
    }
}
