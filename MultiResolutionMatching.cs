using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AudioViewStudio.Analysis
{
    /// <summary>
    /// 다중 해상도 매칭 - Coarse-to-Fine 접근
    /// ★★★ 2026-02-07: 실시간 매칭 최적화 버전 ★★★
    /// </summary>
    public static class MultiResolutionMatching
    {
        // ★ 실시간 매칭용 상수 ★
        private const int DefaultCoarseRegionSize = 10;   // Coarse 단계 region 크기 (초)
        private const int DefaultTopCandidates = 3;       // 상위 후보 개수
        private const int MaxHashOccurrences = 30;        // 너무 흔한 해시 필터링 임계값

        /// <summary>
        /// ★★★ 실시간 매칭용 Coarse-to-Fine 매칭 ★★★
        /// 1단계: Coarse - 전체 referenceIndex에서 region별 매칭 점수 계산, 상위 후보 추출
        /// 2단계: Fine - 후보 region에서만 SFPFM.MatchFingerprints() 정밀 매칭
        /// </summary>
        /// <param name="liveFpts">실시간 수집된 핑거프린트</param>
        /// <param name="referenceIndex">원본 역인덱스 (해시 → 타임스탬프 리스트)</param>
        /// <param name="regionSize">Coarse 단계 region 크기 (초), 기본 10초</param>
        /// <param name="topCandidates">검증할 상위 후보 개수, 기본 3개</param>
        /// <param name="minConfidence">최소 신뢰도 임계값</param>
        /// <returns>최적 매칭 결과</returns>
        public static FingerprintMatchResult MatchCoarseToFineRealtime(
            List<FptEntry> liveFpts,
            Dictionary<ulong, List<int>> referenceIndex,
            int regionSize = DefaultCoarseRegionSize,
            int topCandidates = DefaultTopCandidates,
            double minConfidence = 0.3)
        {
            if (liveFpts == null || liveFpts.Count == 0 || referenceIndex == null || referenceIndex.Count == 0)
            {
                return CreateEmptyResult();
            }

            // ★ 1단계: Coarse 매칭 - region별 매칭 점수 계산 ★
            var candidateRegions = FindCandidateRegionsOptimized(liveFpts, referenceIndex, regionSize);

            if (candidateRegions.Count == 0)
            {
                System.Diagnostics.Debug.WriteLine($"[Coarse-to-Fine] Coarse 단계: 후보 region 없음");
                return CreateEmptyResult();
            }

            // 진단 로그
            System.Diagnostics.Debug.WriteLine($"[Coarse-to-Fine] Coarse 단계: {candidateRegions.Count}개 region 발견, 상위 {Math.Min(topCandidates, candidateRegions.Count)}개 검증");
            for (int i = 0; i < Math.Min(topCandidates, candidateRegions.Count); i++)
            {
                var r = candidateRegions[i];
                System.Diagnostics.Debug.WriteLine($"  [{i + 1}] {r.StartTime}~{r.EndTime}초 (매칭 수: {r.MatchCount})");
            }

            // ★ 2단계: Fine 매칭 - 상위 후보 region에서만 SFPFM.MatchFingerprints 호출 ★
            FingerprintMatchResult bestResult = null;
            int bestRegionStart = 0;

            foreach (var region in candidateRegions.Take(topCandidates))
            {
                var regionResult = MatchInRegionOptimized(liveFpts, referenceIndex, region.StartTime, region.EndTime, minConfidence);

                if (regionResult.IsMatched)
                {
                    if (bestResult == null || regionResult.Confidence > bestResult.Confidence)
                    {
                        bestResult = regionResult;
                        bestRegionStart = region.StartTime;
                    }
                }
            }

            if (bestResult != null && bestResult.IsMatched)
            {
                System.Diagnostics.Debug.WriteLine($"[Coarse-to-Fine] Fine 단계: 매칭 성공! {bestResult.MatchedTime.TotalSeconds:F1}초 (신뢰도: {bestResult.Confidence:P0}, region: {bestRegionStart}~초)");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[Coarse-to-Fine] Fine 단계: 매칭 실패 (상위 {topCandidates}개 region 모두 불합격)");
            }

            return bestResult ?? CreateEmptyResult();
        }

        /// <summary>
        /// 기존 호환용: 2단계 매칭 (coarseIndex 파라미터 유지)
        /// </summary>
        public static FingerprintMatchResult MatchCoarseToFine(
            List<FptEntry> liveFpts,
            Dictionary<ulong, List<int>> referenceIndex,
            Dictionary<ulong, List<int>> coarseIndex, // 더 큰 시간 버킷의 인덱스 (사용 안 함, 호환용)
            double minConfidence = 0.3)
        {
            // coarseIndex 무시하고 referenceIndex만 사용
            return MatchCoarseToFineRealtime(liveFpts, referenceIndex, 
                regionSize: 30, topCandidates: 5, minConfidence: minConfidence);
        }

        /// <summary>
        /// Coarse 단계: region별 매칭 점수 계산 (최적화 버전)
        /// </summary>
        private static List<CandidateRegion> FindCandidateRegionsOptimized(
            List<FptEntry> liveFingerprints,
            Dictionary<ulong, List<int>> referenceIndex,
            int regionSize)
        {
            var regionScores = new Dictionary<int, int>(); // region start -> match count

            foreach (var entry in liveFingerprints)
            {
                if (entry.Hashes == null) continue;

                foreach (var hash in entry.Hashes)
                {
                    if (string.IsNullOrEmpty(hash.Hash)) continue;

                    ulong hashValue = FingerprintHashData_mp.HexStringToUlong(hash.Hash);
                    if (hashValue == 0UL) continue;

                    if (referenceIndex.TryGetValue(hashValue, out var refTimestamps))
                    {
                        // ★ 너무 흔한 해시는 노이즈 → 스킵 ★
                        if (refTimestamps.Count > MaxHashOccurrences) continue;

                        foreach (var refTs in refTimestamps)
                        {
                            int regionStart = (refTs / regionSize) * regionSize;
                            if (!regionScores.ContainsKey(regionStart))
                                regionScores[regionStart] = 0;
                            regionScores[regionStart]++;
                        }
                    }
                }
            }

            return regionScores
                .OrderByDescending(kv => kv.Value)
                .Select(kv => new CandidateRegion
                {
                    StartTime = kv.Key,
                    EndTime = kv.Key + regionSize,
                    MatchCount = kv.Value
                })
                .ToList();
        }

        /// <summary>
        /// Fine 단계: 특정 region에서 SFPFM.MatchFingerprints 호출
        /// </summary>
        private static FingerprintMatchResult MatchInRegionOptimized(
            List<FptEntry> liveFpts,
            Dictionary<ulong, List<int>> referenceIndex,
            int regionStart,
            int regionEnd,
            double minConfidence)
        {
            // ★ 특정 구간의 타임스탬프만 필터링하여 부분 인덱스 생성 ★
            var filteredIndex = new Dictionary<ulong, List<int>>();

            foreach (var kvp in referenceIndex)
            {
                var filteredTimestamps = kvp.Value
                    .Where(t => t >= regionStart && t <= regionEnd)
                    .ToList();

                if (filteredTimestamps.Count > 0)
                {
                    filteredIndex[kvp.Key] = filteredTimestamps;
                }
            }

            if (filteredIndex.Count == 0)
            {
                return CreateEmptyResult();
            }

            // ★ SFPFM.MatchFingerprints로 정밀 매칭 ★
            return SFPFM.MatchFingerprints(
                liveFpts, 
                referenceFpts: null, 
                reverseIndex: filteredIndex, 
                minConfidence: minConfidence,
                maxHashOccurrences: MaxHashOccurrences);
        }

        private struct CandidateRegion
        {
            public int StartTime;
            public int EndTime;
            public int MatchCount;
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

        #region Legacy Methods (기존 호환용)

        private static List<CandidateRegion> FindCandidateRegions(
            List<FptEntry> liveFingerprints,
            Dictionary<ulong, List<int>> coarseIndex,
            int regionSize)
        {
            return FindCandidateRegionsOptimized(liveFingerprints, coarseIndex, regionSize);
        }

        private static FingerprintMatchResult MatchInRegion(
            List<FptEntry> liveFpts,
            Dictionary<ulong, List<int>> referenceIndex,
            int regionStart,
            int regionEnd)
        {
            return MatchInRegionOptimized(liveFpts, referenceIndex, regionStart, regionEnd, 0.3);
        }

        #endregion
    }
}
