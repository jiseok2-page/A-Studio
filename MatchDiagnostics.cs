using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AudioViewStudio.Analysis
{
    /// <summary>
    /// 매칭 진단 정보
    /// </summary>
    public sealed class MatchDiagnostics
    {
        public int TotalLiveHashes { get; set; }
        public int UniqueMatchedHashes { get; set; }
        public int FilteredHashes { get; set; }  // 과다 출현으로 필터링된 해시 수
        public int TotalMatchOccurrences { get; set; }
        public double HashMatchRate { get; set; }
        public double OffsetConcentration { get; set; }

        public Dictionary<int, int> OffsetHistogram { get; set; }
        public List<(int Offset, int Count)> TopOffsets { get; set; }

        public string DiagnosisMessage { get; set; }

        /// <summary>
        /// 진단 정보 생성
        /// </summary>
        /// <param name="maxHashOccurrences">과다 출현 해시 필터링 임계값 (기본: 30)</param>
        public static MatchDiagnostics Analyze(List<FptEntry> liveFpts, Dictionary<ulong, List<int>> referenceIndex, int maxHashOccur = 15)
        {
            var diag = new MatchDiagnostics
            {
                OffsetHistogram = new Dictionary<int, int>(),
                TopOffsets = new List<(int, int)>()
            };

            if (liveFpts == null || liveFpts.Count == 0)
            {
                diag.DiagnosisMessage = "[오류] 라이브 핑거프린트가 비어있습니다.";
                return diag;
            }

            if (referenceIndex == null || referenceIndex.Count == 0)
            {
                diag.DiagnosisMessage = "[오류] 기준 역인덱스가 비어있습니다.";
                return diag;
            }

            int totalHashes = 0;
            int matchedHashes = 0;
            int filteredHashes = 0;  // 필터링된 해시 수
            int totalOccurrences = 0;

            // 라이브 핑거프린트의 각 해시를 기준 역인덱스에서 검색
            foreach (var entry in liveFpts)
            {
                if (entry.Hashes == null) continue;

                foreach (var hash in entry.Hashes)
                {
                    if (string.IsNullOrEmpty(hash.Hash)) continue;

                    totalHashes++;
                    ulong hashValue = FingerprintHashData_mp.HexStringToUlong(hash.Hash);

                    if (hashValue == 0UL) continue;

                    if (referenceIndex.TryGetValue(hashValue, out var refTimestamps))
                    {
                        // ★ 과다 출현 해시 필터링 ★
                        if (refTimestamps.Count > maxHashOccur)
                        {
                            filteredHashes++;
                            continue;  // 이 해시는 건너뜀 (너무 일반적임)
                        }

                        matchedHashes++;

                        foreach (var refTs in refTimestamps)
                        {
                            // 오프셋 = 원본시간 - 실시간시간
                            int offset = refTs - entry.Timestamp;

                            if (!diag.OffsetHistogram.ContainsKey(offset))
                                diag.OffsetHistogram[offset] = 0;

                            diag.OffsetHistogram[offset]++;
                            totalOccurrences++;
                        }
                    }
                }
            }

            diag.TotalLiveHashes = totalHashes;
            diag.UniqueMatchedHashes = matchedHashes;
            diag.FilteredHashes = filteredHashes;
            diag.TotalMatchOccurrences = totalOccurrences;
            diag.HashMatchRate = totalHashes > 0 ? (double)matchedHashes / totalHashes : 0;

            // 상위 10개 오프셋
            diag.TopOffsets = diag.OffsetHistogram
                .OrderByDescending(kv => kv.Value)
                .Take(10)
                .Select(kv => (kv.Key, kv.Value))
                .ToList();

            // ★★★ 수정된 오프셋 집중도 계산 (CalcOffsetConcentration과 동일) ★★★
            // 인접 오프셋 병합(±1초) 및 matchedHashes를 분모로 사용
            if (diag.TopOffsets.Count > 0 && matchedHashes > 0)
            {
                int bestOffset = diag.TopOffsets[0].Offset;
                int topCount = diag.TopOffsets[0].Count;
                
                // 인접 오프셋(±1초) 병합
                int mergedCount = topCount;
                if (diag.OffsetHistogram.ContainsKey(bestOffset - 1))
                    mergedCount += diag.OffsetHistogram[bestOffset - 1];
                if (diag.OffsetHistogram.ContainsKey(bestOffset + 1))
                    mergedCount += diag.OffsetHistogram[bestOffset + 1];
                
                // 분모를 matchedHashes로 변경 (totalOccurrences 대신)
                diag.OffsetConcentration = (double)mergedCount / matchedHashes;
            }

            // 진단 메시지 생성
            diag.DiagnosisMessage = GenerateDiagnosisMessage(diag);

            return diag;
        }

        private static string GenerateDiagnosisMessage(MatchDiagnostics diag)
        {
            var sb = new StringBuilder();

            sb.AppendLine($"=== 매칭 진단 결과 ===");
            sb.AppendLine($"총 라이브 해시: {diag.TotalLiveHashes}");
            sb.AppendLine($"유효 매칭 해시: {diag.UniqueMatchedHashes} ({diag.HashMatchRate:P1})");
            if (diag.FilteredHashes > 0)
            {
                sb.AppendLine($"필터링된 해시: {diag.FilteredHashes} (과다 출현으로 제외됨)");
            }
            sb.AppendLine($"총 매칭 횟수: {diag.TotalMatchOccurrences}");

            if (diag.HashMatchRate < 0.1)
            {
                sb.AppendLine("\n[경고] 해시 매칭률이 매우 낮습니다.");
                sb.AppendLine("- 오디오 품질 문제일 수 있습니다.");
                sb.AppendLine("- 기준 핑거프린트와 다른 콘텐츠일 수 있습니다.");
            }

            if (diag.TopOffsets.Count > 0)
            {
                sb.AppendLine($"\n상위 오프셋:");
                foreach (var (offset, count) in diag.TopOffsets.Take(5))
                {
                    sb.AppendLine($"  {offset}초: {count}회");
                }

                sb.AppendLine($"\n오프셋 집중도: {diag.OffsetConcentration:P1}");

                if (diag.OffsetConcentration < 0.3)
                {
                    sb.AppendLine("[경고] 오프셋이 분산되어 있습니다. 정확한 매칭이 어려울 수 있습니다.");
                }
                else if (diag.OffsetConcentration > 0.7)
                {
                    sb.AppendLine("[양호] 오프셋이 집중되어 있어 신뢰할 수 있는 매칭입니다.");
                }
            }

            return sb.ToString();
        }
    }

}
