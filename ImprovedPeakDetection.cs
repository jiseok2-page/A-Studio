using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AudioViewStudio.Analysis
{
    // 현재 구현의 문제점:
    // - 고정된 neighborhood 크기
    // - 주파수 대역별 특성 미반영
    // - Spectral Flatness 미고려

    // 개선된 Peak Detection
    public static class ImprovedPeakDetection
    {
        // Mel-scale 기반 주파수 대역 정의
        // ★★★ 2026-02-07: 고주파 대역 제거 (마이크 노이즈 민감) ★★★
        // 6400Hz 이상의 Brilliance 대역 제거: 마이크/Loopback 캡처에서 고주파 노이즈가 Peak로 잡힘
        // 영화 오디오의 핵심 주파수는 보통 300~6000Hz
        private static readonly (double Min, double Max)[] FrequencyBands = new[]
        {
        (0.0, 200.0),      // Sub-bass
        (200.0, 400.0),    // Bass
        (400.0, 800.0),    // Low-mid
        (800.0, 1600.0),   // Mid
        (1600.0, 3200.0),  // Upper-mid
        (3200.0, 6400.0)   // Presence (최대 6400Hz)
        // (6400.0, 20000.0)  // Brilliance - ★ 제거됨: 노이즈 민감 ★
    };

        /// <summary>
        /// 대역별 적응형 Peak 검출
        /// </summary>
        public static void DetectPeaksAdaptive(
            ConcurrentBag<SFPFM.Peak> peaks,
            double[] magnitudes,
            double[] frequencies,
            double time,
            FingerprintConfig config)
        {
            if (magnitudes.Length == 0) return;

            // 1. 전역 통계 계산
            var stats = CalculateSpectralStatistics(magnitudes);

            // 2. Spectral Flatness 계산 (음악 vs 노이즈 구분)
            double spectralFlatness = CalculateSpectralFlatness(magnitudes);

            // 노이즈가 많은 구간은 더 엄격한 임계값 적용
            double flatnessMultiplier = spectralFlatness > 0.8 ? 1.5 : 1.0;

            // 3. 대역별 Peak 검출
            foreach (var band in FrequencyBands)
            {
                DetectPeaksInBand(peaks, magnitudes, frequencies, time,
                    band.Min, band.Max, stats, flatnessMultiplier, config);
            }
        }

        private static void DetectPeaksInBand(
            ConcurrentBag<SFPFM.Peak> peaks,
            double[] magnitudes,
            double[] frequencies,
            double time,
            double minFreq,
            double maxFreq,
            SpectralStatistics globalStats,
            double flatnessMultiplier,
            FingerprintConfig config)
        {
            // 대역 내 인덱스 범위 찾기
            int startIdx = -1, endIdx = -1;
            for (int i = 0; i < frequencies.Length; i++)
            {
                if (frequencies[i] >= minFreq && startIdx < 0) startIdx = i;
                if (frequencies[i] <= maxFreq) endIdx = i;
            }

            if (startIdx < 0 || endIdx < startIdx) return;

            // 대역 내 통계 계산
            double bandSum = 0, bandMax = 0;
            int bandCount = endIdx - startIdx + 1;

            for (int i = startIdx; i <= endIdx; i++)
            {
                bandSum += magnitudes[i];
                if (magnitudes[i] > bandMax) bandMax = magnitudes[i];
            }

            double bandMean = bandSum / bandCount;

            // 대역별 적응형 임계값
            // 저주파는 에너지가 높으므로 상대적으로 높은 임계값
            double freqRatio = (minFreq + maxFreq) / 2.0 / 10000.0;
            double bandThreshold = bandMean + config.PeakThresholdMultiplier *
                (1.0 + freqRatio) * flatnessMultiplier *
                Math.Sqrt(CalculateBandVariance(magnitudes, startIdx, endIdx, bandMean));

            bandThreshold = Math.Max(bandThreshold, bandMax * 0.15);

            // 적응형 neighborhood 크기 (저주파는 더 넓게)
            int neighborhood = (int)(config.PeakNeighborhoodSize * (1.0 + (1.0 - freqRatio)));
            neighborhood = Math.Max(3, Math.Min(neighborhood, 10));

            // Peak 검출
            var candidates = new List<(int idx, double mag, double freq)>();

            for (int i = startIdx + neighborhood; i <= endIdx - neighborhood; i++)
            {
                if (magnitudes[i] < bandThreshold) continue;

                bool isLocalMax = true;
                for (int offset = -neighborhood; offset <= neighborhood && isLocalMax; offset++)
                {
                    if (offset != 0 && magnitudes[i + offset] >= magnitudes[i])
                        isLocalMax = false;
                }

                if (isLocalMax)
                {
                    // Parabolic interpolation으로 정확한 주파수 추정
                    double refinedFreq = RefineFrequencyParabolic(
                        magnitudes, frequencies, i);
                    candidates.Add((i, magnitudes[i], refinedFreq));
                }
            }

            // 대역당 상위 N개만 선택
            int maxPeaksPerBand = Math.Max(2, config.MaxPeaksPerFrame / FrequencyBands.Length);
            foreach (var c in candidates.OrderByDescending(x => x.mag).Take(maxPeaksPerBand))
            {
                peaks.Add(new SFPFM.Peak
                {
                    Time = time,
                    Frequency = c.freq,
                    Magnitude = c.mag
                });
            }
        }

        /// <summary>
        /// Parabolic interpolation으로 Peak 주파수 정밀화
        /// </summary>
        private static double RefineFrequencyParabolic(
            double[] magnitudes, double[] frequencies, int peakIdx)
        {
            if (peakIdx <= 0 || peakIdx >= magnitudes.Length - 1)
                return frequencies[peakIdx];

            double y0 = Math.Log(magnitudes[peakIdx - 1] + 1e-10);
            double y1 = Math.Log(magnitudes[peakIdx] + 1e-10);
            double y2 = Math.Log(magnitudes[peakIdx + 1] + 1e-10);

            double delta = 0.5 * (y0 - y2) / (y0 - 2 * y1 + y2 + 1e-10);
            delta = Math.Max(-0.5, Math.Min(0.5, delta));

            double freqStep = frequencies[peakIdx] - frequencies[peakIdx - 1];
            return frequencies[peakIdx] + delta * freqStep;
        }

        /// <summary>
        /// Spectral Flatness 계산 (Wiener Entropy)
        /// 값이 1에 가까우면 노이즈, 0에 가까우면 음악/음성
        /// </summary>
        private static double CalculateSpectralFlatness(double[] magnitudes)
        {
            if (magnitudes.Length == 0) return 0;

            double geometricMean = 0;
            double arithmeticMean = 0;
            int count = 0;

            for (int i = 0; i < magnitudes.Length; i++)
            {
                if (magnitudes[i] > 1e-10)
                {
                    geometricMean += Math.Log(magnitudes[i]);
                    arithmeticMean += magnitudes[i];
                    count++;
                }
            }

            if (count == 0) return 0;

            geometricMean = Math.Exp(geometricMean / count);
            arithmeticMean /= count;

            if (arithmeticMean < 1e-10) return 0;
            return geometricMean / arithmeticMean;
        }

        private static double CalculateBandVariance(
            double[] magnitudes, int start, int end, double mean)
        {
            double sumSq = 0;
            for (int i = start; i <= end; i++)
            {
                double diff = magnitudes[i] - mean;
                sumSq += diff * diff;
            }
            return sumSq / (end - start + 1);
        }

        private static SpectralStatistics CalculateSpectralStatistics(double[] magnitudes)
        {
            double sum = 0, sumSq = 0, max = 0;
            for (int i = 0; i < magnitudes.Length; i++)
            {
                sum += magnitudes[i];
                sumSq += magnitudes[i] * magnitudes[i];
                if (magnitudes[i] > max) max = magnitudes[i];
            }

            double mean = sum / magnitudes.Length;
            double variance = (sumSq / magnitudes.Length) - (mean * mean);

            return new SpectralStatistics
            {
                Mean = mean,
                StdDev = Math.Sqrt(Math.Max(0, variance)),
                Max = max
            };
        }

        private struct SpectralStatistics
        {
            public double Mean;
            public double StdDev;
            public double Max;
        }
    }

}
