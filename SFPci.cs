using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AudioViewStudio.Analysis
{
    /// <summary>
    /// SFPci (Sound Fingerprint Common Interface) 모듈
    /// 핑거프린트 관련 공통 유틸리티 및 헬퍼 메서드를 제공합니다.
    /// </summary>
    public static class SFPci
    {
        /// <summary>
        /// "HHmmss.fff" 형식의 문자열을 TimeSpan으로 변환합니다.
        /// </summary>
        /// <param name="hhmmssDotMs">"HHmmss.fff" 형식의 시간 문자열 (예: "143005.123")</param>
        /// <param name="result">변환된 TimeSpan (출력)</param>
        /// <returns>변환 성공 여부</returns>
        public static bool ConvertToTimeSpan(string hhmmssDotMs, out TimeSpan result)
        {
            result = TimeSpan.Zero;
            
            // null 또는 빈 문자열 체크
            if (string.IsNullOrWhiteSpace(hhmmssDotMs)) { return false; }

            // 입력 형식 검증: "HHmmss.fff" 형식이어야 함 (최소 6자리 + 점 + 3자리 = 10자리)
            // 예: "143005.123" (10자리)
            int dotIndex = hhmmssDotMs.IndexOf('.');
            if (dotIndex != 6 || hhmmssDotMs.Length < 10)
            {
                // 형식이 맞지 않음
                return false;
            }
            double millsec = Convert.ToDouble(hhmmssDotMs.Substring(dotIndex + 1));
            double second = Convert.ToDouble(hhmmssDotMs.Substring(4, 2));
            double minute = Convert.ToDouble(hhmmssDotMs.Substring(2, 2));
            double hour = Convert.ToDouble(hhmmssDotMs.Substring(0, 2));
           
            DateTime now = DateTime.Now;
            DateTime bas = now.AddHours(-now.Hour).AddMinutes(-now.Minute).AddSeconds(-now.Second).AddMilliseconds(-now.Millisecond); 
            DateTime picked = bas.AddHours(hour).AddMinutes(minute).AddSeconds(second).AddMilliseconds(millsec); 
            result = picked - bas;

            return true;
        }

        /// <summary>
        /// 역인덱스 생성 (핑거프린트 로드 시 역인덱스가 없는 경우)
        /// </summary>
        public static Dictionary<ulong, List<int>> BuildReverseIndex(List<FptEntry> fingerprints)
        {
            var index = new Dictionary<ulong, List<int>>();

            if (fingerprints == null) return index;

            foreach (var entry in fingerprints)
            {
                if (entry.Hashes == null) continue;

                foreach (var hash in entry.Hashes)
                {
                    if (string.IsNullOrEmpty(hash.Hash)) continue;

                    ulong hashValue = FingerprintHashData_mp.HexStringToUlong(hash.Hash);
                    if (hashValue == 0UL) continue;

                    if (!index.TryGetValue(hashValue, out var timestamps))
                    {
                        timestamps = new List<int>();
                        index[hashValue] = timestamps;
                    }
                    timestamps.Add(entry.Timestamp);
                }
            }

            return index;
        }


        /// <summary>
        /// 파일명에 사용할 수 없는 문자를 제거합니다.
        /// </summary>
        /// <param name="fileName">원본 파일명</param>
        /// <returns>정리된 파일명</returns>
        public static string SanitizeFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return "Unknown";
            }

            char[] invalidChars = Path.GetInvalidFileNameChars();
            string sanitized = fileName;

            foreach (char c in invalidChars)
            {
                sanitized = sanitized.Replace(c, '_');
            }

            // 너무 긴 파일명 처리
            if (sanitized.Length > 200)
            {
                sanitized = sanitized.Substring(0, 200);
            }

            return sanitized.Trim();
        }

        /// <summary>
        /// TimeSpan을 "HH:mm:ss" 형식의 문자열로 포맷팅합니다.
        /// </summary>
        /// <param name="timeSpan">포맷팅할 TimeSpan</param>
        /// <returns>포맷팅된 문자열</returns>
        public static string FormatTimeSpan(TimeSpan timeSpan)
        {
            return $"{(int)timeSpan.TotalHours:D2}:{timeSpan.Minutes:D2}:{timeSpan.Seconds:D2}";
        }

        /// <summary>
        /// 로딩 시간을 mm:ss.ms 형식으로 포맷팅합니다.
        /// </summary>
        /// <param name="timeSpan">로딩 시간</param>
        /// <returns>mm:ss.ms 형식의 문자열 (예: 01:23.456)</returns>
        public static string FormatLoadingTime(TimeSpan timeSpan)
        {
            int totalSeconds = (int)timeSpan.TotalSeconds;
            int minutes = totalSeconds / 60;
            int seconds = totalSeconds % 60;
            int milliseconds = timeSpan.Milliseconds;
            if(minutes == 0)
            {
                return $"{seconds:D2}.{milliseconds:D3}";
            }
            else
            {
                return $"{minutes:D2}:{seconds:D2}.{milliseconds:D3}";
            }
        }

        /// <summary>
        /// 파일 크기를 읽기 쉬운 형식으로 포맷팅합니다.
        /// </summary>
        /// <param name="bytes">바이트 수</param>
        /// <returns>포맷팅된 문자열 (예: "1.5 MB")</returns>
        public static string FormatSize(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double size = bytes;
            int unit = 0;
            while (size >= 1024 && unit < units.Length - 1)
            {
                size /= 1024;
                unit++;
            }

            return $"{size:0.##} {units[unit]}";
        }

        /// <summary>
        /// 시간 범위 문자열("30초", "1분", "2분" 등)을 TimeSpan으로 변환합니다.
        /// </summary>
        /// <param name="value">시간 범위 문자열</param>
        /// <returns>변환된 TimeSpan (기본값: 3분)</returns>
        public static TimeSpan ParseTimeRangeString(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return TimeSpan.FromMinutes(3); // 기본값 3분
            }

            string trimmedValue = value.Trim();
            
            if (trimmedValue.EndsWith("초"))
            {
                string digits = new string(trimmedValue.Where(char.IsDigit).ToArray());
                if (double.TryParse(digits, out double seconds))
                {
                    return TimeSpan.FromSeconds(seconds);
                }
            }
            else if (trimmedValue.EndsWith("분"))
            {
                string digits = new string(trimmedValue.Where(char.IsDigit).ToArray());
                if (double.TryParse(digits, out double minutes))
                {
                    return TimeSpan.FromMinutes(minutes);
                }
            }

            return TimeSpan.FromMinutes(3); // 기본값 3분
        }

        /// <summary>
        /// 문자열에서 숫자만 추출하여 초 단위로 파싱합니다.
        /// </summary>
        /// <param name="value">파싱할 문자열</param>
        /// <param name="seconds">파싱된 초 값 (출력)</param>
        /// <returns>파싱 성공 여부</returns>
        public static bool TryParseSeconds(string value, out double seconds)
        {
            seconds = 0;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            string digits = new string(value.Where(char.IsDigit).ToArray());
            return double.TryParse(digits, out seconds);
        }

        /// <summary>
        /// Picked 핑거프린트 파일명을 구성합니다.
        /// </summary>
        /// <param name="movieID">영화 ID</param>
        /// <param name="pickTime">선택한 시간</param>
        /// <param name="termMs">구간 길이 (밀리초)</param>
        /// <param name="extension">파일 확장자 (예: ".mpack", ".json")</param>
        /// <param name="featureDir">특징 파일 디렉토리</param>
        /// <returns>파일 경로</returns>
        public static string ComposePickedFilename(string movieID, TimeSpan pickTime, int termMs, string extension, string featureDir)
        {
            var pickTimeHMS = $"{pickTime.Hours:D2}{pickTime.Minutes:D2}{pickTime.Seconds:D2}.{pickTime.Milliseconds:D3}";
            // 파일명 생성: movieID_pickTime_term.fp.mpack
            // 파일명에 사용할 수 없는 문자 제거 (영화 ID에도 특수 문자가 있을 수 있음)
            string sanitizedID = SanitizeFileName(movieID);
            string fileName = $"{sanitizedID}_{pickTimeHMS}_{termMs}.fp{extension}";

            // 저장 디렉토리 확인 (featureDir 사용)
            string saveDir = featureDir;
            if (string.IsNullOrWhiteSpace(saveDir) || !Directory.Exists(saveDir))
            {
                saveDir = System.Windows.Forms.Application.StartupPath;
            }
            string filePath = Path.Combine(saveDir, fileName);
            return filePath;
        }

        /// <summary>
        /// 매칭 신뢰도가 0%일 때, picked FP 파일명에서 시간 정보를 추출하여
        /// 해당 시점 주변의 영화 핑거프린트 해시들을 디버그용 파일로 저장합니다.
        /// </summary>
        /// <param name="pickedFpPath">picked 핑거프린트 파일 전체 경로</param>
        /// <param name="pickedFileName">dgvPickedFeatures에 표시된 파일명</param>
        /// <param name="movieFp">영화 핑거프린트 리스트</param>
        /// <param name="audioSrcInfo">오디오 소스 정보</param>
        public static void SaveMovieHashesForDebugWhenNoMatch(
            string pickedFpPath, 
            string pickedFileName, 
            List<FptEntry> movieFp,
            AudioFptFileInfo audioSrcInfo)
        {
            try
            {
                if (movieFp == null || movieFp.Count == 0)
                {
                    return;
                }

                // 파일명에서 pickTimeMs, termMs 추출
                // 형식: movieID_pickTimeMs_termMs.fp.json
                string nameWithoutExt1 = Path.GetFileNameWithoutExtension(pickedFileName);   // *.fp
                string nameWithoutExt2 = Path.GetFileNameWithoutExtension(nameWithoutExt1);  // movieID_pickTimeMs_termMs
                string[] parts = nameWithoutExt2.Split('_');
                if (parts.Length < 3)
                {
                    return;
                }

                // parts[0] = movieID, parts[1] = pickTimeMs, parts[2] = termMs
                string timeMs = string.Copy(parts[1]);
                if (!ConvertToTimeSpan(timeMs, out TimeSpan result))
                    return;
                var pickTimeMs = result.TotalMilliseconds;

                if (!int.TryParse(parts[2], out int termMs))
                {
                    termMs = 0; // termMs는 디버그 정보일 뿐이므로 실패해도 계속 진행
                }

                double pickTimeSec = pickTimeMs / 1000.0;
                int targetTimestamp = (int)Math.Floor(pickTimeSec);
                int windowSec = 2; // ±2초 범위
                int fromTs = Math.Max(0, targetTimestamp - windowSec);
                int toTs = targetTimestamp + windowSec;

                var movieEntriesInWindow = movieFp
                    .Where(fp => fp.Timestamp >= fromTs && fp.Timestamp <= toTs)
                    .OrderBy(fp => fp.Timestamp)
                    .ToList();

                // 디버그용 출력 경로
                string dir = Path.GetDirectoryName(pickedFpPath);
                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                {
                    dir = audioSrcInfo?.featureDir;
                    if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                    {
                        dir = System.Windows.Forms.Application.StartupPath;
                    }
                }

                string debugFileName = $"{Path.GetFileNameWithoutExtension(nameWithoutExt2)}_movieHashes_debug.txt";
                string debugFilePath = Path.Combine(dir, debugFileName);

                using (var writer = new StreamWriter(debugFilePath, false, Encoding.UTF8))
                {
                    writer.WriteLine("==== Movie Fingerprint Hashes Debug ====");
                    writer.WriteLine($"Picked file   : {pickedFileName}");
                    writer.WriteLine($"PickTimeMs    : {pickTimeMs} ms (~{pickTimeSec:F3} s)");
                    writer.WriteLine($"TermMs        : {termMs} ms");
                    writer.WriteLine($"Time window   : [{fromTs}, {toTs}] sec");
                    writer.WriteLine($"TotalEntries  : {movieFp.Count}");
                    writer.WriteLine($"EntriesInWindow: {movieEntriesInWindow.Count}");
                    writer.WriteLine();
                    // 현재 F-Mode 설정(fftSize, hopSize, hashOnly)을 참고용으로 기록
                    if (audioSrcInfo != null)
                    {
                        writer.WriteLine($"FMode fftSize : {audioSrcInfo.fftSize}");
                        writer.WriteLine($"FMode hopSize : {audioSrcInfo.hopSize}");
                        writer.WriteLine($"FMode hashOnly: {audioSrcInfo.mvHashOnly}");
                        writer.WriteLine();
                    }

                    foreach (var entry in movieEntriesInWindow)
                    {
                        writer.WriteLine($"-- Timestamp: {entry.Timestamp} sec --");
                        if (entry.Hashes == null || entry.Hashes.Count == 0)
                        {
                            writer.WriteLine("  (no hashes)");
                            writer.WriteLine();
                            continue;
                        }

                        writer.WriteLine($"  HashCount: {entry.Hashes.Count}");
                        writer.WriteLine("  Hashes:");
                        foreach (var h in entry.Hashes)
                        {
                            writer.WriteLine($"{h.Hash}");
                        }
                        writer.WriteLine();
                    }
                }
            }
            catch
            {
                // 디버그용이므로 예외는 무시
            }
        }
    }
}

