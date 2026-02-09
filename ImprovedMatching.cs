using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AudioViewStudio.Analysis
{
    /// <summary>
    /// 기하학적 검증을 포함한 개선된 매칭
    /// </summary>
    public static class ImprovedMatching
    {
        /// <summary>
        /// RANSAC 기반 기하학적 검증
        /// 시간 오프셋의 일관성을 검증하여 False Positive 감소
        /// </summary>
        public static FingerprintMatchResult MatchWithGeometricVerification(
            List<FptEntry> liveFingerprints,
            Dictionary<ulong, List<int>> referenceIndex,
            double minConfidence = 0.3,
            int ransacIterations = 100)
        {
            if (liveFingerprints == null || liveFingerprints.Count == 0 ||
                referenceIndex == null || referenceIndex.Count == 0)
            {
                return CreateEmptyResult();
            }

            // 1단계: 모든 매칭 쌍 수집
            var matchPairs = CollectMatchPairs(liveFingerprints, referenceIndex);

            if (matchPairs.Count < 3)
            {
                return CreateEmptyResult();
            }

            // 2단계: RANSAC으로 일관된 오프셋 찾기
            var ransacResult = RunRANSAC(matchPairs, ransacIterations);

            if (ransacResult.InlierCount < 3)
            {
                return CreateEmptyResult();
            }

            // 3단계: Inlier들로 정밀한 오프셋 계산
            double refinedOffset = RefineOffset(ransacResult.Inliers);

            // 4단계: 신뢰도 계산
            double confidence = CalculateConfidence(
                ransacResult.InlierCount,
                matchPairs.Count,
                liveFingerprints.Sum(e => e.Hashes?.Count ?? 0));

            return new FingerprintMatchResult
            {
                IsMatched = confidence >= minConfidence,
                Confidence = confidence,
                MatchedTime = TimeSpan.FromSeconds(Math.Max(0, refinedOffset)),
                MatchedHashCount = ransacResult.InlierCount,
                TotalHashCount = matchPairs.Count
            };
        }

        private struct MatchPair
        {
            public int LiveTimestamp;
            public int RefTimestamp;
            public int Offset => RefTimestamp - LiveTimestamp;
        }

        private struct RANSACResult
        {
            public List<MatchPair> Inliers;
            public int InlierCount;
            public double BestOffset;
        }

        private static List<MatchPair> CollectMatchPairs(
            List<FptEntry> liveFingerprints,
            Dictionary<ulong, List<int>> referenceIndex)
        {
            // ★ 성능 개선: 매칭 쌍 수 제한 ★
            const int MaxHashesPerEntry = 50;      // 각 엔트리에서 최대 해시 수
            const int MaxRefTimestamps = 20;       // 각 해시당 최대 참조 타임스탬프 수
            const int MaxTotalPairs = 50000;       // 최대 총 쌍 수
            
            var pairs = new List<MatchPair>();

            foreach (var entry in liveFingerprints)
            {
                if (entry.Hashes == null) continue;

                // 해시 수 제한
                var hashesToProcess = entry.Hashes.Count <= MaxHashesPerEntry
                    ? entry.Hashes
                    : entry.Hashes.Take(MaxHashesPerEntry).ToList();

                foreach (var hash in hashesToProcess)
                {
                    if (string.IsNullOrEmpty(hash.Hash)) continue;

                    ulong hashValue = FingerprintHashData_mp.HexStringToUlong(hash.Hash);
                    if (hashValue == 0UL) continue;

                    if (referenceIndex.TryGetValue(hashValue, out var refTimestamps))
                    {
                        // 참조 타임스탬프 수 제한
                        var tsToProcess = refTimestamps.Count <= MaxRefTimestamps
                            ? refTimestamps
                            : refTimestamps.Take(MaxRefTimestamps).ToList();
                        
                        foreach (var refTs in tsToProcess)
                        {
                            pairs.Add(new MatchPair
                            {
                                LiveTimestamp = entry.Timestamp,
                                RefTimestamp = refTs
                            });

                            // 최대 쌍 수 도달 시 조기 종료
                            if (pairs.Count >= MaxTotalPairs)
                            {
                                return pairs;
                            }
                        }
                    }
                }
            }

            return pairs;
        }

        private static RANSACResult RunRANSAC(List<MatchPair> pairs, int iterations)
        {
            // ★ 성능 개선: 오프셋 히스토그램 방식으로 변경 (O(n) 한 번 순회) ★
            const int offsetTolerance = 1; // ±1초 허용

            if (pairs.Count == 0)
            {
                return new RANSACResult
                {
                    Inliers = new List<MatchPair>(),
                    InlierCount = 0,
                    BestOffset = 0
                };
            }

            // 1단계: 오프셋 히스토그램 생성 (O(n))
            var offsetHistogram = new Dictionary<int, int>();
            foreach (var pair in pairs)
            {
                int offset = pair.Offset;
                if (!offsetHistogram.ContainsKey(offset))
                    offsetHistogram[offset] = 0;
                offsetHistogram[offset]++;
            }

            // 2단계: 최빈 오프셋 찾기 (±1초 그룹화)
            int bestOffset = 0;
            int maxCount = 0;
            
            foreach (var kvp in offsetHistogram)
            {
                // 현재 오프셋과 ±1초 범위의 카운트 합산
                int groupCount = kvp.Value;
                if (offsetHistogram.TryGetValue(kvp.Key - 1, out int c1)) groupCount += c1;
                if (offsetHistogram.TryGetValue(kvp.Key + 1, out int c2)) groupCount += c2;
                
                if (groupCount > maxCount)
                {
                    maxCount = groupCount;
                    bestOffset = kvp.Key;
                }
            }

            // 3단계: 최빈 오프셋에 해당하는 Inliers 수집
            var inliers = new List<MatchPair>();
            foreach (var pair in pairs)
            {
                if (Math.Abs(pair.Offset - bestOffset) <= offsetTolerance)
                {
                    inliers.Add(pair);
                }
            }

            return new RANSACResult
            {
                Inliers = inliers,
                InlierCount = inliers.Count,
                BestOffset = bestOffset
            };
        }

        private static double RefineOffset(List<MatchPair> inliers)
        {
            if (inliers.Count == 0) return 0;

            // 가중 평균 (중앙값도 고려)
            var offsets = inliers.Select(p => (double)p.Offset).OrderBy(o => o).ToList();

            // 중앙값 사용 (이상치에 강건)
            int mid = offsets.Count / 2;
            if (offsets.Count % 2 == 0)
                return (offsets[mid - 1] + offsets[mid]) / 2.0;
            else
                return offsets[mid];
        }

        private static double CalculateConfidence(
            int inlierCount, int totalMatches, int totalLiveHashes)
        {
            // 여러 지표의 가중 조합
            double inlierRatio = totalMatches > 0
                ? (double)inlierCount / totalMatches
                : 0;

            double coverageRatio = totalLiveHashes > 0
                ? (double)inlierCount / totalLiveHashes
                : 0;

            // 최소 매칭 수에 따른 보정
            double countFactor = Math.Min(1.0, inlierCount / 10.0);

            return (inlierRatio * 0.4 + coverageRatio * 0.3 + countFactor * 0.3);
        }

        private static FingerprintMatchResult CreateEmptyResult()
        {
            return new FingerprintMatchResult
            {
                IsMatched = false,
                Confidence = 0,
                MatchedTime = TimeSpan.Zero,
                MatchedHashCount = 0,
                TotalHashCount = 0
            };
        }
    }

}
