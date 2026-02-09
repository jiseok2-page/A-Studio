using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AudioViewStudio.Analysis
{
    /// <summary>
    /// 실시간 스트리밍 매칭 클래스
    /// </summary>
    public class RealtimeFingerprintMatcher
    {
        private readonly Dictionary<ulong, List<int>> _referenceIndex;
        private readonly Queue<FptEntry> _recentFingerprints;
        private readonly int _windowSizeSeconds;
        private readonly int _slideIntervalSeconds;

        private int _consecutiveMatches;
        private int _lastMatchedOffset;
        
        // ★★★ 오프셋 안정성 검증 상수 ★★★
        private const int SmallOffsetChangeThreshold = 2;   // 연속 매칭 허용 범위 (±2초)
        private const int LargeOffsetChangeThreshold = 10;  // 큰 변화 감지 임계값 (10초 이상)
        private List<int> _recentOffsets = new List<int>(); // 최근 오프셋 히스토리 (안정성 확인용)
        private const int RequiredStableOffsets = 3;        // 안정 판정에 필요한 일관된 오프셋 수

        public RealtimeFingerprintMatcher(
            Dictionary<ulong, List<int>> referenceIndex,
            int windowSizeSeconds = 5,
            int slideIntervalSeconds = 1)
        {
            _referenceIndex = referenceIndex;
            _windowSizeSeconds = windowSizeSeconds;
            _slideIntervalSeconds = slideIntervalSeconds;
            _recentFingerprints = new Queue<FptEntry>();
            _consecutiveMatches = 0;
            _lastMatchedOffset = int.MinValue;
            _recentOffsets = new List<int>();  // ★ 오프셋 히스토리 초기화 ★
        }

        /// <summary>
        /// 새로운 핑거프린트 추가 및 매칭 시도
        /// </summary>
        public RealtimeMatchResult AddFingerprint(FptEntry newEntry)
        {
            // 윈도우에 추가
            _recentFingerprints.Enqueue(newEntry);

            // 오래된 엔트리 제거
            while (_recentFingerprints.Count > 0)
            {
                var oldest = _recentFingerprints.Peek();
                if (newEntry.Timestamp - oldest.Timestamp > _windowSizeSeconds)
                {
                    _recentFingerprints.Dequeue();
                }
                else
                {
                    break;
                }
            }

            // 매칭 수행
            var windowEntries = _recentFingerprints.ToList();
            
            // ★ 타임스탬프 진단 ★
            if (windowEntries.Count > 0)
            {
                var timestamps = windowEntries.Select(e => e.Timestamp).OrderBy(t => t).ToList();
                System.Diagnostics.Debug.WriteLine($"[RealtimeMatcher] 윈도우 엔트리: {windowEntries.Count}개, 타임스탬프 범위: {timestamps.First()} ~ {timestamps.Last()}초");
            }
            
            var matchResult = PerformWindowMatch(windowEntries);

            // ★★★ 연속 매칭 검증 강화: 오프셋 안정성 기반 ★★★
            if (matchResult.IsMatched)
            {
                int currentOffset = (int)matchResult.MatchedTime.TotalSeconds;
                int offsetChange = Math.Abs(currentOffset - _lastMatchedOffset - _slideIntervalSeconds);

                // ★ 오프셋 변화량 분석 ★
                if (_lastMatchedOffset != int.MinValue)
                {
                    if (offsetChange >= LargeOffsetChangeThreshold)
                    {
                        // ★★★ 큰 오프셋 변화 감지 (10초 이상) → 완전 리셋 + 경고 ★★★
                        System.Diagnostics.Debug.WriteLine($"[RealtimeMatcher] ⚠️ 큰 오프셋 변화 감지!");
                        System.Diagnostics.Debug.WriteLine($"  이전: {_lastMatchedOffset}초 → 현재: {currentOffset}초 (변화: {offsetChange}초)");
                        System.Diagnostics.Debug.WriteLine($"  → 오프셋 히스토리 리셋, 연속 카운트 1로 초기화");
                        _recentOffsets.Clear();
                        _consecutiveMatches = 1;
                    }
                    else if (offsetChange <= SmallOffsetChangeThreshold)
                    {
                        // ★ 작은 변화 (±2초 이내) → 연속 매칭 ★
                        _consecutiveMatches++;
                    }
                    else
                    {
                        // ★ 중간 변화 (3~9초) → 연속성 끊김, 카운트 유지하되 증가 안함 ★
                        System.Diagnostics.Debug.WriteLine($"[RealtimeMatcher] 중간 오프셋 변화: {offsetChange}초 → 연속 카운트 유지 ({_consecutiveMatches})");
                    }
                }
                else
                {
                    // 첫 매칭
                    _consecutiveMatches = 1;
                }

                // ★ 오프셋 히스토리 관리 ★
                _recentOffsets.Add(currentOffset);
                if (_recentOffsets.Count > RequiredStableOffsets)
                    _recentOffsets.RemoveAt(0);

                // ★ 오프셋 안정성 검증: 최근 오프셋들이 일관되는지 확인 ★
                bool isOffsetStable = false;
                if (_recentOffsets.Count >= RequiredStableOffsets)
                {
                    // 최근 오프셋들의 변화량 합계 계산
                    int totalVariation = 0;
                    for (int i = 1; i < _recentOffsets.Count; i++)
                    {
                        totalVariation += Math.Abs(_recentOffsets[i] - _recentOffsets[i - 1] - _slideIntervalSeconds);
                    }
                    // 평균 변화량이 2초 이내면 안정
                    double avgVariation = (double)totalVariation / (_recentOffsets.Count - 1);
                    isOffsetStable = avgVariation <= SmallOffsetChangeThreshold;
                    
                    if (!isOffsetStable)
                    {
                        System.Diagnostics.Debug.WriteLine($"[RealtimeMatcher] 오프셋 불안정: 평균 변화 {avgVariation:F1}초 > {SmallOffsetChangeThreshold}초");
                    }
                }

                _lastMatchedOffset = currentOffset;
                
                return new RealtimeMatchResult
                {
                    IsMatched = matchResult.IsMatched,
                    Confidence = matchResult.Confidence,
                    MatchedOriginalTime = matchResult.MatchedTime,
                    ConsecutiveMatchCount = _consecutiveMatches,
                    // ★ 안정 매칭 조건 강화: 연속 3회 + 오프셋 안정성 확인 ★
                    IsStableMatch = _consecutiveMatches >= RequiredStableOffsets && isOffsetStable
                };
            }
            else
            {
                _consecutiveMatches = 0;
                // 매칭 실패 시 히스토리 유지 (일시적 실패 허용)
            }

            return new RealtimeMatchResult
            {
                IsMatched = matchResult.IsMatched,
                Confidence = matchResult.Confidence,
                MatchedOriginalTime = matchResult.MatchedTime,
                ConsecutiveMatchCount = _consecutiveMatches,
                IsStableMatch = false
            };
        }

        /// <summary>
        /// 우세 비율 임계값: 1위 오프셋이 2위보다 이 비율 이상 많아야 매칭 인정
        /// ★★★ 2026-02-08: 1.50 → 1.20으로 완화 (낮은 집중도 환경 지원) ★★★
        /// 이전: 1.50 → 정답도 거부됨 (803초 52회 vs Top 7229초 134회)
        /// </summary>
        private const double MinDominanceRatio = 1.20;

        private FingerprintMatchResult PerformWindowMatch(List<FptEntry> windowEntries)
        {
            // ★★★ 2026-02-02: 최소 윈도우 시간 설정 ★★★
            // 문제: 초기 몇 초의 데이터가 불충분하여 잘못된 오프셋 선택
            // 해결: 최소 5초 이상 데이터가 누적된 후 매칭 시도
            if (windowEntries.Count < 5)
            {
                System.Diagnostics.Debug.WriteLine($"[RealtimeMatcher] 윈도우 엔트리: {windowEntries.Count}개, 타임스탬프 범위: {windowEntries.Min(e => e.Timestamp)} ~ {windowEntries.Max(e => e.Timestamp)}초");
                return new FingerprintMatchResult { IsMatched = false };
            }
            
            // ★ 신뢰도 임계값 조정: 다시 완화하여 매칭 후보를 넓게 받음 ★
            var result = SFPFM.MatchFingerprints(windowEntries, null, _referenceIndex, minConfidence: 0.2, maxHashOccurrences: 30);
            
            // ★★★ 2026-02-02 v4: 시간적 일관성 검증 추가 ★★★
            // 단순 빈도 대신 "연속된 타임스탬프에서 일관된 오프셋"을 찾음
            if (result.IsMatched)
            {
                // Step 1: 각 타임스탬프별로 오프셋 수집 (시간적 일관성 검증용)
                var timestampOffsets = new Dictionary<int, List<int>>(); // timestamp -> [offsets]
                var offsetCounts = new Dictionary<int, int>(); // offset -> count (기존 로직)
                int totalValidHashes = 0;

                foreach (var entry in windowEntries)
                {
                    if (entry.Hashes == null) continue;
                    
                    var entryOffsets = new List<int>();
                    
                    foreach (var hashData in entry.Hashes)
                    {
                        if (string.IsNullOrEmpty(hashData.Hash)) continue;
                        ulong hashValue = FingerprintHashData_mp.HexStringToUlong(hashData.Hash);
                        if (hashValue == 0UL) continue;
                        
                        if (_referenceIndex.TryGetValue(hashValue, out var refTimes))
                        {
                            if (refTimes.Count > 30) continue; // 너무 흔한 해시 제외

                            foreach (var refTime in refTimes)
                            {
                                int offset = refTime - entry.Timestamp;
                                
                                // 기존: 전체 빈도 계산
                                if (!offsetCounts.ContainsKey(offset))
                                    offsetCounts[offset] = 0;
                                offsetCounts[offset]++;
                                totalValidHashes++;
                                
                                // 신규: 타임스탬프별 오프셋 수집
                                entryOffsets.Add(offset);
                            }
                        }
                    }
                    
                    if (entryOffsets.Count > 0)
                    {
                        timestampOffsets[entry.Timestamp] = entryOffsets;
                    }
                }

                if (offsetCounts.Count < 2)
                {
                    if (offsetCounts.Count == 1)
                        System.Diagnostics.Debug.WriteLine($"[RealtimeMatcher] 단일 오프셋 발견 → 통과");
                    return result;
                }

                // Step 2: 인접 오프셋 클러스터링
                // ★★★ 2026-02-05: tolerance 10초 → 3초 축소 (정밀한 오프셋 분리) ★★★
                var clusteredCounts = ClusterOffsets(offsetCounts, tolerance: 3);
                
                // Step 3: 시간적 일관성 점수 계산
                var consistencyScores = CalculateConsistencyScores(timestampOffsets, clusteredCounts.Keys.ToList(), tolerance: 3);
                
                // Step 4: 최종 점수 = 빈도 × (1 + 일관성 점수)
                // ★★★ 2026-02-05: 일관성 가중치 2.0 → 0.5 (순수 빈도 우선) ★★★
                var finalScores = new Dictionary<int, double>();
                foreach (var kvp in clusteredCounts)
                {
                    double consistency = consistencyScores.ContainsKey(kvp.Key) ? consistencyScores[kvp.Key] : 0;
                    finalScores[kvp.Key] = kvp.Value * (1.0 + consistency * 0.5); // 일관성 가중치 완화
                }
                
                var topTwo = finalScores.OrderByDescending(x => x.Value).Take(2).ToList();
                int firstOffset = topTwo[0].Key;
                double firstScore = topTwo[0].Value;
                int firstCount = clusteredCounts[firstOffset];
                int secondOffset = topTwo.Count > 1 ? topTwo[1].Key : 0;
                double secondScore = topTwo.Count > 1 ? topTwo[1].Value : 0;
                int secondCount = topTwo.Count > 1 ? clusteredCounts[secondOffset] : 0;
                
                double dominanceRatio = secondScore > 0 ? firstScore / secondScore : double.MaxValue;
                double density = totalValidHashes > 0 ? (double)firstCount / totalValidHashes : 0;
                double consistency1 = consistencyScores.ContainsKey(firstOffset) ? consistencyScores[firstOffset] : 0;
                
                System.Diagnostics.Debug.WriteLine($"[RealtimeMatcher] 오프셋 검증: 우세비 {dominanceRatio:F2}, 밀도 {density:P2} ({firstCount}/{totalValidHashes}), 일관성 {consistency1:P0}");
                System.Diagnostics.Debug.WriteLine($"  1위: {firstOffset}초 (점수:{firstScore:F1}), 2위: {secondOffset}초 (점수:{secondScore:F1}), SFPFM결과: {(int)result.MatchedTime.TotalSeconds}초");

                // 검증 1: 우세 비율
                if (dominanceRatio < MinDominanceRatio)
                {
                    System.Diagnostics.Debug.WriteLine($"[실시간 매칭] ⚠️ 우세 비율 부족 ({dominanceRatio:F2}x < {MinDominanceRatio}x) → 매칭 거부");
                    return new FingerprintMatchResult { IsMatched = false };
                }
                
                // 검증 2: 밀도 (완화됨)
                const double MinDensity = 0.003; // 0.3%
                if (density < MinDensity)
                {
                    System.Diagnostics.Debug.WriteLine($"[실시간 매칭] ⚠️ 밀도 부족 ({density:P2} < {MinDensity:P2}) → 매칭 거부 (노이즈)");
                    return new FingerprintMatchResult { IsMatched = false };
                }

                // 검증 3: 시간적 일관성 (신규!)
                // ★★★ 2026-02-07: 임계값 15% → 40% 강화 (오매칭 방지) ★★★
                const double MinConsistency = 0.40; // 최소 40% 일관성 필요
                if (consistency1 < MinConsistency)
                {
                    System.Diagnostics.Debug.WriteLine($"[실시간 매칭] ⚠️ 시간적 일관성 부족 ({consistency1:P0} < {MinConsistency:P0}) → 매칭 거부 (노이즈)");
                    return new FingerprintMatchResult { IsMatched = false };
                }

                // 검증 4: SFPFM 결과와 히스토그램 일치
                int resultOffset = (int)result.MatchedTime.TotalSeconds;
                bool isConsistent = (Math.Abs(resultOffset - firstOffset) <= 3) || (Math.Abs(resultOffset - secondOffset) <= 3);
                
                if (!isConsistent)
                {
                    // SFPFM 결과 대신 히스토그램 1위 사용
                    System.Diagnostics.Debug.WriteLine($"[실시간 매칭] ℹ️ SFPFM 결과({resultOffset}초) 대신 히스토그램 1위({firstOffset}초) 사용");
                    result = new FingerprintMatchResult
                    {
                        IsMatched = true,
                        MatchedTime = TimeSpan.FromSeconds(firstOffset),
                        Confidence = result.Confidence
                    };
                }

                System.Diagnostics.Debug.WriteLine($"[실시간 매칭] ✅ 검증 통과 (우세비 {dominanceRatio:F2}, 일관성 {consistency1:P0})");
            } 
            
            return result;
        }
        
        /// <summary>
        /// 인접 오프셋 클러스터링 (±tolerance 초 내의 오프셋을 병합)
        /// </summary>
        private Dictionary<int, int> ClusterOffsets(Dictionary<int, int> offsetCounts, int tolerance)
        {
            var sorted = offsetCounts.OrderByDescending(x => x.Value).ToList();
            var clustered = new Dictionary<int, int>();
            var used = new HashSet<int>();
            
            foreach (var kvp in sorted)
            {
                if (used.Contains(kvp.Key)) continue;
                
                int clusterCenter = kvp.Key;
                int clusterCount = kvp.Value;
                
                // 인접 오프셋 병합
                for (int delta = -tolerance; delta <= tolerance; delta++)
                {
                    int neighbor = kvp.Key + delta;
                    if (neighbor != kvp.Key && offsetCounts.ContainsKey(neighbor) && !used.Contains(neighbor))
                    {
                        clusterCount += offsetCounts[neighbor];
                        used.Add(neighbor);
                    }
                }
                
                clustered[clusterCenter] = clusterCount;
                used.Add(clusterCenter);
            }
            
            return clustered;
        }
        
        /// <summary>
        /// 시간적 일관성 점수 계산
        /// 연속된 타임스탬프에서 같은 오프셋이 나오면 점수 증가
        /// </summary>
        private Dictionary<int, double> CalculateConsistencyScores(
            Dictionary<int, List<int>> timestampOffsets, 
            List<int> candidateOffsets,
            int tolerance)
        {
            var scores = new Dictionary<int, double>();
            var sortedTimestamps = timestampOffsets.Keys.OrderBy(x => x).ToList();
            
            foreach (var offset in candidateOffsets)
            {
                int consecutiveCount = 0;
                int maxConsecutive = 0;
                int totalPresent = 0;
                
                foreach (var ts in sortedTimestamps)
                {
                    // 이 타임스탬프에서 해당 오프셋(±tolerance)이 나왔는지 확인
                    bool found = timestampOffsets[ts].Any(o => Math.Abs(o - offset) <= tolerance);
                    
                    if (found)
                    {
                        consecutiveCount++;
                        totalPresent++;
                        maxConsecutive = Math.Max(maxConsecutive, consecutiveCount);
                    }
                    else
                    {
                        consecutiveCount = 0;
                    }
                }
                
                // 일관성 점수 = 최대 연속 출현 / 전체 타임스탬프 수
                double consistency = sortedTimestamps.Count > 0 
                    ? (double)maxConsecutive / sortedTimestamps.Count 
                    : 0;
                    
                scores[offset] = consistency;
            }
            
            return scores;
        }
    }

    /// <summary>
    /// 실시간 매칭 결과
    /// </summary>
    public sealed class RealtimeMatchResult
    {
        public bool IsMatched { get; set; }
        public double Confidence { get; set; }
        public TimeSpan MatchedOriginalTime { get; set; }
        public int ConsecutiveMatchCount { get; set; }
        public bool IsStableMatch { get; set; }
    }

}
