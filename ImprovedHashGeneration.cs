using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AudioViewStudio.Analysis
{
    /// <summary>
    /// 개선된 해시 생성 - 64비트 해시로 충돌 최소화
    /// ★★★ 32비트 → 64비트 전환 ★★★
    /// - 해시 공간: 2^32 (~43억) → 2^64 (~1.8×10^19)
    /// - 해시 충돌: 사실상 제거
    /// - 오프셋 집중도: 대폭 향상 예상 (60~80%)
    /// </summary>
    public static class ImprovedHashGeneration
    {
        // 64비트 해시 시드 (FNV-1a 64비트)
        private static readonly ulong HashSeed64 = 0xCBF29CE484222325UL;
        private const ulong FnvPrime64 = 0x100000001B3UL;

        /// <summary>
        /// 64비트 로버스트 해시 생성
        /// ★★★ 2026-02-02 v3: 대칭 전략 (원본/Live 모두 변형 해시 생성) ★★★
        /// - 원본: 역인덱스에 등록
        /// - Live: 역인덱스에서 조회 (각 변형 해시로)
        /// - forIndexing 파라미터는 하위 호환성을 위해 유지 (실제로 무시됨)
        /// </summary>
        public static List<ulong> GenerateRobustHashes64(SFPFM.Peak peak1, SFPFM.Peak peak2, bool forIndexing = false)
        {
            double timeDelta = peak2.Time - peak1.Time;
            double freqRatio = peak2.Frequency / (peak1.Frequency + 1e-10);

            // 양자화
            int timeQ = QuantizeTime(timeDelta);
            int freqRatioQ = QuantizeFreqRatio(freqRatio);
            int f1Band = GetMelBand(peak1.Frequency);
            int f2Band = GetMelBand(peak2.Frequency);

            // ★ 기본 64비트 해시 ★
            ulong baseHash = ComputeHashFNV1a64(timeQ, freqRatioQ, f1Band, f2Band);

            // ★★★ 2026-02-02 v3: 대칭 전략으로 전환 ★★★
            // 비대칭 전략(원본만 변형)이 효과 없음 → Live에서도 변형 해시 생성
            // 원본과 Live 모두 변형 해시를 생성하여 매칭 확률 극대화
            //
            // forIndexing=true (원본): 역인덱스에 등록
            // forIndexing=false (Live): 역인덱스에서 조회 (각 변형 해시로)
            
            // ★★★ 2026-02-08: 최소 변형 (시간만 ±1) ★★★
            // 변형 없음: 매칭 3회 (너무 낮음)
            // 변형 ±2: 매칭 25회 (해시 충돌 심각)
            // 타협: 시간만 ±1 변형 → 3개 해시 → 해시 충돌 최소화 + 매칭률 유지
            
            var hashes = new HashSet<ulong> { baseHash };
            
            // 시간 ±1 버킷 변형만 적용 (마이크 캡처의 미세한 시간 차이 흡수)
            hashes.Add(ComputeHashFNV1a64(timeQ - 1, freqRatioQ, f1Band, f2Band));
            hashes.Add(ComputeHashFNV1a64(timeQ + 1, freqRatioQ, f1Band, f2Band));
            
            return hashes.ToList();
        }

        /// <summary>
        /// [하위 호환용] 32비트 해시 생성 - 기존 코드 호환을 위해 유지
        /// 내부적으로 64비트 해시의 하위 32비트를 반환
        /// </summary>
        public static List<uint> GenerateRobustHashes(SFPFM.Peak peak1, SFPFM.Peak peak2, bool forIndexing = false)
        {
            var hashes64 = GenerateRobustHashes64(peak1, peak2, forIndexing);
            return hashes64.Select(h => (uint)(h & 0xFFFFFFFF)).ToList();
        }

        /// <summary>
        /// Mel-scale 기반 주파수 대역 분류 
        /// ★★★ 2026-02-08: 50개 대역 (해시 공간 확대) ★★★
        /// 이전: 10개 대역 → 해시 충돌 심각
        /// </summary>
        private static int GetMelBand(double frequency)
        {
            double mel = 2595.0 * Math.Log10(1.0 + frequency / 700.0);
            int band = (int)(mel / 80.0);  // 80 mel 단위 (0~6400Hz → 약 50개 대역)
            return Math.Max(0, Math.Min(49, band)); // 0~49 (50개 대역)
        }

        /// <summary>
        /// 시간 양자화 (선형, 고해상도)
        /// ★★★ 2026-02-08: ~100개 버킷 (해시 공간 확대) ★★★
        /// 이전: ~25개 버킷 → 해시 충돌 심각
        /// </summary>
        private static int QuantizeTime(double timeDelta)
        {
            // 0.03초 단위로 양자화 (0~3초 범위 = 100개 버킷)
            return Math.Max(0, Math.Min(99, (int)(timeDelta / 0.03)));
        }

        /// <summary>
        /// 주파수 비율 양자화 (로그 스케일, 고해상도)
        /// ★★★ 2026-02-08: 100개 버킷 (해시 공간 확대) ★★★
        /// 이전: 24개 버킷 → 해시 충돌 심각
        /// </summary>
        private static int QuantizeFreqRatio(double ratio)
        {
            double logRatio = Math.Log(Math.Max(0.25, Math.Min(4.0, ratio)));
            // 100개 버킷: logRatio 범위 [-1.4, 1.4] → [0, 100]
            return Math.Max(0, Math.Min(99, (int)((logRatio + 1.4) * 35.7)));
        }

        /// <summary>
        /// FNV-1a 64비트 해시 계산
        /// ★★★ 64비트로 확장하여 충돌 최소화 ★★★
        /// </summary>
        private static ulong ComputeHashFNV1a64(int a, int b, int c, int d)
        {
            ulong hash = HashSeed64;

            // 각 값을 바이트 단위로 해싱 (더 균일한 분포)
            hash ^= (ulong)(a & 0xFF);
            hash *= FnvPrime64;
            hash ^= (ulong)((a >> 8) & 0xFF);
            hash *= FnvPrime64;

            hash ^= (ulong)(b & 0xFF);
            hash *= FnvPrime64;
            hash ^= (ulong)((b >> 8) & 0xFF);
            hash *= FnvPrime64;

            hash ^= (ulong)(c & 0xFF);
            hash *= FnvPrime64;

            hash ^= (ulong)(d & 0xFF);
            hash *= FnvPrime64;

            return hash;
        }
    }
}
