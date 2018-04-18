using System;
using System.Collections.Generic;
using System.Xml;
using System.Linq;
using System.Net;
using System.IO;
using System.Threading;

namespace ImageDownloader
{
    class Program
    {
        private static string _help =
            "usage: " + typeof(Program).Assembly.GetName().Name + "[options]\n" +
            "options:\n" +
            "    -i, --in file               input XML file\n" +
            "    -o, --out directory         output directory - default: out\n" +
            "    -u, --use useAttrib         USE attribute of target fileGrp - default: DEFAULT\n" +
            "    -t, --threads numThreads    number of threads for downloading files - default: 1\n" +
            "    -h, --help                  shows this help\n";

        private static string _in = "";
        private static string _out = "out";
        private static string _use = "DEFAULT";
        private static int _numThreads = 1;
        private static int _totalImages = 0;
        private static int _imagesDownloaded = 0;
        private static object _printLock = new object();

        static void Main(string[] args)
        {
            var start = DateTime.Now;

            _checkArgs(args);

            // gather iamge links from input xml
            var images = _getLinks();

            if (images.Count() == 0)
            {
                _fail("Could not find any valid nodes.");
                return;
            }

            _totalImages = images.Count();

            // fix IIIF links to request highest quality
            if (_use == "IIIF")
                images = _fixIiifLinks(images);

            // prepare output dir
            Directory.CreateDirectory(_out);

            // split links for threading
            var threadWorkloads = _splitImageList(images);

            // download images
            var threads = new List<Thread>();

            foreach(var workload in threadWorkloads)
            {
                var thread = new Thread(new ParameterizedThreadStart(_download));
                thread.Start(workload);
                threads.Add(thread);
            }

            foreach (var thread in threads)
                thread.Join();

            var end = DateTime.Now;
            var time = end - start;

            Console.WriteLine(string.Format("Finished in {0} hours, {1} minutes and {2} seconds.", time.Hours, time.Minutes, time.Seconds));
        }

        private static void _fail(string message) => Console.WriteLine(message + " Use -h for help.");

        private static void _checkArgs(string[] args)
        {
            for(var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                switch (arg)
                {
                    case "--file":
                    case "-f":
                    case "--in":
                    case "-i":
                        if (i < args.Length - 1)
                        {
                            _in = args[++i];
                            Console.WriteLine("Using input file " + _in);
                        }
                        break;
                    case "--out":
                    case "-o":
                        if (i < args.Length - 1)
                        {
                            _out = args[++i];
                            if (!_out.EndsWith('/'))
                                _out += '/';
                            Console.WriteLine("Using output dir " + _out);
                        }
                        break;
                    case "--use":
                    case "-u":
                        if (i < args.Length - 1)
                        {
                            _use = args[++i].ToUpper();
                            Console.WriteLine("Using input file " + _out);
                        }
                        break;
                    case "--threads":
                    case "-t":
                        if (i < args.Length - 1)
                        {
                            _numThreads = int.Parse(args[++i]);
                            Console.WriteLine("Using " + _numThreads + " threads");
                        }
                        break;
                    case "--help":
                    case "-h":
                        Console.WriteLine(_help);
                        break;
                    default:
                        _fail("Unknown parameter: " + arg + ".");
                        break;
                }
            }
        }
        
        private static IEnumerable<Image> _getLinks()
        {
            // check input file, load doc
            if(_in == "")
            {
                _fail("No input file specified.");
                return new List<Image>();
            }
            var doc = new XmlDocument();
            try
            {
                doc.Load(_in);
            }
            catch (Exception ex)
            {
                _fail("Could not read input file. Error: " + ex.Message + ".");
                return new List<Image>();
            }
            
            // read xml namespaces
            var nsmgr = new XmlNamespaceManager(new NameTable());
            foreach(XmlAttribute attr in doc.SelectSingleNode("/*").Attributes)
                if(attr.Name.StartsWith("xmlns:"))
                    nsmgr.AddNamespace(attr.Name.Split(':')[1], attr.Value);
            
            // gather image links
            return doc.SelectNodes("//mets:fileGrp[@USE='" + _use + "']/mets:file", nsmgr).Cast<XmlNode>()
                .Select(n => new Image() {
                    Url = n.FirstChild.Attributes.GetNamedItem("xlink:href").Value,
                    Id = n.Attributes.GetNamedItem("ID").Value });
        }

        private static IEnumerable<Image> _fixIiifLinks(IEnumerable<Image> images)
        {
            var fixedImages = new List<Image>(images.Count());

            Console.WriteLine("Fixing IIIF links...");
            foreach (var image in images)
            {
                Console.WriteLine("Old: " + image.Url);
                var split = image.Url.Split('/');
                var index = split.Count() - 1;
                // quality and type
                split[index--] = "default.tif";
                // rotation
                split[index--] = "0";
                // size
                split[index--] = "full";
                // region
                split[index--] = "full";
                image.Url = string.Join('/', split);
                Console.WriteLine("New: " + image.Url);

                fixedImages.Add(new Image() { Id = image.Id, Url = image.Url });
            }

            return fixedImages;
        }

        private static IEnumerable<IEnumerable<Image>> _splitImageList(IEnumerable<Image> images)
        {
            int imagesPerThread = (int)Math.Ceiling((float)images.Count() / _numThreads);

            var result = new List<IEnumerable<Image>>();

            for(var i = 0; i < _numThreads; i++)
            {
                var section = images.Skip(i * imagesPerThread).Take(imagesPerThread);
                // if not last section, take given amount - if last section, just take all remaining
                if (i <= _numThreads - 1)
                    section = section.Take(imagesPerThread);
                result.Add(section);
            }

            return result;
        }

        private static void _download(object imagesObject)
        {
            var images = (IEnumerable<Image>)imagesObject;

            HttpWebRequest request;
            HttpWebResponse response;
            foreach (var image in images)
            {
                Console.WriteLine("Downloading " + image.Url + "...");
                // request image
                request = WebRequest.CreateHttp(image.Url);
                try
                {
                    response = (HttpWebResponse)request.GetResponse();
                }
                catch (WebException ex)
                {
                    response = (HttpWebResponse)ex.Response;
                }

                // check success
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    Console.WriteLine("Could not fetch " + image.Id + ". Status code: " + (int)response.StatusCode + " (" + response.StatusCode + ").");
                    continue;
                }

                // save image
                using (var sw = new StreamWriter(_out + image.Id.Replace(_use + "_", "") + _getFileType(response.ContentType)))
                {
                    response.GetResponseStream().CopyTo(sw.BaseStream);
                    sw.Close();
                }

                lock (_printLock)
                {
                    _imagesDownloaded++;
                    Console.WriteLine("Successfully downloaded " + image.Id + ". Total progress: "  + _imagesDownloaded + "/" + _totalImages + " images.");
                }

                
            }
        }

        private static string _getFileType(string contentType)
        {
            switch (contentType)
            {
                case "image/jpeg":
                    return ".jpg";
                case "image/png":
                    return ".png";
                case "image/tif":
                    return ".tif";
                case "application/pdf":
                    return ".pdf";
                default:
                    Console.WriteLine("Unknown format: " + contentType);
                    return ".bin";
            }
        }
    }

    internal class Image
    {
        public string Url;
        public string Id;
    }
}
