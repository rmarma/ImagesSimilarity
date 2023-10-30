using System;
using System.Collections.Generic;
using System.Text;
using System.Drawing;
using System.IO;
using System.Runtime.Caching;
using System.Threading.Tasks;

namespace ImagesSimilarity
{
    sealed class Program
    {
        private const string FMT_LOG = "{0}: {1}";
        private const string LOG_MAIN = "Main";
        private const string LOG_BITMAP = "Bitmap";

        static void Main(string[] args)
        {
            if (args != null && args.Length > 0)
            {
                Console.WriteLine(FMT_LOG, LOG_MAIN, args.Length);
                new BitmapSimilarity(args).Start();
            }
            else
            {
                Console.WriteLine(FMT_LOG, LOG_MAIN, "Empty");
                Console.WriteLine(FMT_LOG, LOG_MAIN, "Drag and drop images to .exe");
            }
            Console.WriteLine(FMT_LOG, LOG_MAIN, "Ended");
            Console.ReadKey();
        }

        private sealed class BitmapSimilarity
        {

            private const int IMG_MAX_SIZE = 640;

            private const int SIMILARITY_THRESHOLD = 30;
            private const double SIMILARITY_THRESHOLD_LAB = 0.1;

            private readonly string[] paths;
            private readonly Queue<Task> tasks;
            private readonly IDictionary<string, IList<double>> results;
            private readonly IDictionary<string, FileInfo> filesInfo;

            private readonly object lockerFileInfo = new object();
            private readonly object lockerResults = new object();

            private readonly int maxTasks;

            public BitmapSimilarity(string[] paths)
            {
                this.paths = paths;
                tasks = new Queue<Task>();
                results = new Dictionary<string, IList<double>>(paths.Length);
                filesInfo = new Dictionary<string, FileInfo>(paths.Length);
                maxTasks = Environment.ProcessorCount * 4;
            }

            public void Start()
            {
                var dateTimeStart = DateTime.Now;
                Console.WriteLine(FMT_LOG, LOG_BITMAP, "Started Sync " + dateTimeStart);
                Array.Sort(paths);
                Console.WriteLine(FMT_LOG, LOG_BITMAP, "Sorted " + (DateTime.Now - dateTimeStart).TotalSeconds);

                for (int i = 0; i < paths.Length; ++i)
                {
                    tasks.Enqueue(StartTask(i));

                    if (tasks.Count >= maxTasks)
                    {
                        do
                        {
                            var t = tasks.Dequeue();
                            t.Wait();
                        } while (tasks.Count > 0);

                        GC.Collect();
                    }
                }
                while (tasks.Count > 0)
                {
                    var t = tasks.Dequeue();
                    t.Wait();
                }

                Console.WriteLine(FMT_LOG, LOG_BITMAP, "Calculated " + DateTime.Now + " (" + (DateTime.Now - dateTimeStart).ToString() + ")");
                SaveResults();
                Console.WriteLine(FMT_LOG, LOG_BITMAP, "Saved " + (DateTime.Now - dateTimeStart).ToString());
            }

            private Task StartTask(int index)
            {
                return Task.Factory.StartNew(() =>
                {
                    string p1 = paths[index];
                    FileInfo f1 = GetFileInfo(p1);
                    BitmapColor[] c1 = GetColors(p1);
                    Console.WriteLine(FMT_LOG, LOG_BITMAP, f1.Name);
                    for (int j = index + 1; j < paths.Length; ++j)
                    {
                        string p2 = paths[j];
                        FileInfo f2 = GetFileInfo(p2);
                        BitmapColor[] c2 = GetColors(p2);
                        AddResult(p1, GetSimilarity(c1, c2), paths.Length - index - 1);
                    }
                });
            }

            private void SaveResults()
            {
                StringBuilder sb = new StringBuilder();
                string p;
                bool f;
                IList<double> list;
                for (int i = 0; i < paths.Length - 1; ++i)
                {
                    p = paths[i];
                    if (!results.ContainsKey(p)) continue;
                    f = false;
                    list = results[p];
                    sb.AppendLine(GetFileInfo(p).Name);
                    for (int j = 1; j < list.Count; ++j)
                    {
                        if (f)
                        {
                            if (list[j] < list[j - 1])
                            {
                                f = false;
                                sb.AppendLine(GetFileInfo(paths[i + j]).Name + " " + list[j - 1]);
                            }
                        }
                        else
                        {
                            if (list[j] > list[j - 1])
                            {
                                f = true;
                                // если последний элемент списка
                                if (j == list.Count - 1)
                                {
                                    sb.AppendLine(GetFileInfo(paths[i + j + 1]).Name + " " + list[j]);
                                }
                            }
                        }
                    }
                    sb.AppendLine();
                }
                File.WriteAllText("similarity.txt", sb.ToString());
            }

            private void AddResult(string p, double result, int capacity = 16)
            {
                lock (lockerResults)
                {
                    if (!results.ContainsKey(p))
                    {
                        results.Add(p, new List<double>(capacity));
                    }
                    results[p].Add(result);
                }
            }

            private BitmapColor[] GetColors(string path)
            {
                lock (path)
                {
                    var cache = MemoryCache.Default;
                    BitmapColor[] color = cache[path] as BitmapColor[];
                    if (color == null)
                    {
                        using (var b0 = new Bitmap(path))
                        {
                            var s0 = b0.Size;
                            int coef = Math.Max(Math.Max(b0.Width, b0.Height) / IMG_MAX_SIZE, 1);
                            var s1 = new Size(s0.Width / coef, s0.Height / coef);
                            using (var b = new Bitmap(b0, s1))
                            {
                                color = new BitmapColor[b.Width * b.Height];
                                int index = 0;
                                for (int y = 0; y < b.Height; ++y)
                                {
                                    for (int x = 0; x < b.Width; ++x, ++index)
                                    {
                                        color[index] = FromColor(b.GetPixel(x, y));
                                    }
                                }
                                var policy = new CacheItemPolicy();
                                policy.SlidingExpiration = TimeSpan.FromMinutes(3);
                                cache.Add(path, color, policy);
                            }
                        }
                    }
                    return color;
                }
            }

            private FileInfo GetFileInfo(string path)
            {
                lock (lockerFileInfo)
                {
                    if (!filesInfo.ContainsKey(path))
                    {
                        filesInfo.Add(path, new FileInfo(path));
                    }
                    return filesInfo[path];
                }
            }

            private BitmapColor FromColor(Color color)
            {
                return new BitmapColor(color);
            }

            public static double GetSimilarity(BitmapColor[] c1, BitmapColor[] c2)
            {
                double result = 0;
                for (int i = 0; i < Math.Min(c1.Length, c2.Length); ++i)
                {
                    result += GetSimilarity(c1[i], c2[i]);
                }
                return result / c1.Length;
            }

            public static double GetSimilarityLab(BitmapColor c1, BitmapColor c2)
            {
                double dist = Math.Sqrt((c1.x - c2.x) * (c1.x - c2.x) + (c1.y - c2.y) * (c1.y - c2.y) + (c1.z - c2.z) * (c1.z - c2.z));
                if (dist < SIMILARITY_THRESHOLD_LAB)
                {
                    dist = 0.0;
                }
                return 1.0 - dist;
            }

            public static double GetSimilarity(BitmapColor c1, BitmapColor c2)
            {
                double result = GetSimilarity(c1.a, c2.a);
                result += GetSimilarity(c1.r, c2.r);
                result += GetSimilarity(c1.g, c2.g);
                result += GetSimilarity(c1.b, c2.b);
                return result / 4.0;
            }

            public static double GetSimilarity(byte b1, byte b2, int threshold = SIMILARITY_THRESHOLD)
            {
                int delta = Math.Abs(b2 - b1);
                if (delta < threshold)
                {
                    delta = 0;
                }
                return (255.0 - delta) / 255.0;
            }
        }

        private sealed class BitmapTask
        {
            private readonly string p1;
            private readonly string p2;
            private readonly FileInfo f1;
            private readonly FileInfo f2;
            private readonly BitmapColor[] c1;
            private readonly BitmapColor[] c2;

            private double result;
            private bool hasResult;
            private bool isStarted;

            private readonly object locker = new object();

            private readonly Task task;

            public BitmapTask(string p1, string p2, FileInfo f1, FileInfo f2, BitmapColor[] c1, BitmapColor[] c2)
            {
                this.p1 = p1;
                this.p2 = p2;
                this.f1 = f1;
                this.f2 = f2;
                this.c1 = c1;
                this.c2 = c2;
                task = new Task(Process);
            }

            public static BitmapTask Start(string p1, string p2, FileInfo f1, FileInfo f2, BitmapColor[] c1, BitmapColor[] c2)
            {
                var task = new BitmapTask(p1, p2, f1, f2, c1, c2);
                task.Start();
                return task;
            }

            public void Start()
            {
                if (!HasResult() && !IsStarted())
                {
                    SetStarted(true);
                    task.Start();
                }
            }

            public bool HasResult()
            {
                lock (locker)
                {
                    return hasResult;
                }
            }

            private void SetHasResult(bool has)
            {
                lock (locker)
                {
                    hasResult = has;
                }
            }

            public bool IsStarted()
            {
                lock (locker)
                {
                    return isStarted;
                }
            }

            private void SetStarted(bool started)
            {
                lock (locker)
                {
                    isStarted = started;
                }
            }

            public double GetResult()
            {
                lock (locker)
                {
                    return result;
                }
            }

            private void SetResult(double result)
            {
                lock (locker)
                {
                    this.result = result;
                }
            }

            private void Process()
            {
                SetResult(BitmapSimilarity.GetSimilarity(c1, c2));
                SetHasResult(true);
            }

            public void Join()
            {
                task.Wait();
            }

            public string GetPath1()
            {
                return p1;
            }

            public string GetPath2()
            {
                return p2;
            }

            public string GetFullNameFile1()
            {
                return f1.FullName;
            }

            public string GetNameFile2()
            {
                return f2.Name;
            }
        }

        private struct BitmapColor
        {
            public readonly byte a;
            public readonly byte r;
            public readonly byte g;
            public readonly byte b;

            public readonly float h;
            public readonly float s;
            public readonly float v;

            public readonly double x;
            public readonly double y;
            public readonly double z;

            public BitmapColor(Color color)
            {
                a = color.A;
                r = color.R;
                g = color.G;
                b = color.B;
                h = color.GetHue();
                s = color.GetSaturation();
                v = color.GetBrightness();

                x = 0.4124 * (color.R / 255.0) + 0.3576 * (color.G / 255.0) + 0.1805 * (color.B / 255.0);
                y = 0.2126 * (color.R / 255.0) + 0.7152 * (color.G / 255.0) + 0.0722 * (color.B / 255.0);
                z = 0.0193 * (color.R / 255.0) + 0.1192 * (color.G / 255.0) + 0.9505 * (color.B / 255.0);
            }
        }
    }
}
