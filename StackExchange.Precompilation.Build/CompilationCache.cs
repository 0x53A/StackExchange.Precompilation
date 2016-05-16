using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using StackExchange.Precompilation;
using System.Security.Cryptography;
using System.IO;
using System.Collections;
using Microsoft.CodeAnalysis;
using System.Diagnostics;
using System.Reflection;

namespace StackExchange.Precompilation
{
    public class CompilationCache : ICompilationCache
    {
        string _cacheDir;
        public CompilationCache(string cacheDir)
        {
            _cacheDir = cacheDir;
        }

        public async Task<Guid> CalculateHash(string[] commandLine, CSharpCommandLineArguments cscArgs, List<ICompileModule> compilationModules)
        {
            //Debugger.Launch();
            var hasher = Hydra.getHashActorRef

            var verboseCache = new StringBuilder();
            verboseCache.AppendLine("CompilationModules:");

            var types = (from m in compilationModules select m.GetType()).ToList();
            types.Sort();
            verboseCache.AppendLine(" Types:");
            foreach (var t in types)
                verboseCache.AppendFormat("  {0}\n", t.FullName);
            var assemblyHashes = (from a in types select new { a.Assembly, hash = Hasher.ComputeHash(File.OpenRead(a.Assembly.Location)) })
                                    .ToDictionary(key => key.Assembly, val => val.hash);
            verboseCache.AppendLine(" Assemblies:");
            foreach (var a in assemblyHashes.OrderBy(kv=>kv.Key))
                verboseCache.AppendFormat("  {0}->{1}\n", a.Key, new Guid(a.Value));

            var hashOfCompilationModules = Hasher.Combine(compilationModules.SelectMany(m => new[] { Hasher.ComputeHash(m.GetType().FullName), assemblyHashes[m.GetType().Assembly] }));
            verboseCache.AppendFormat(" Full: {0}\n", new Guid(hashOfCompilationModules).ToString());
            // TODO: add all assemblies referenced by the modules

            // TODO: the 'ICompileModule's could access other files,
            // add an interface which asks the 'ICompileModule' for the hashes of its additional files (e.g. localized resources)

            // the normal options are all included by hashing the command line, so we just need to hash the content of all relevant files.
            var filesToHash = new Dictionary<string, object>
            {
                {"AdditionalFiles", cscArgs.AdditionalFiles },
                {"AnalyzerReferences", cscArgs.AnalyzerReferences },
                {"CompilationOptions.CryptoKeyContainer", cscArgs.CompilationOptions.CryptoKeyContainer },
                {"CompilationOptions.CryptoKeyFile", cscArgs.CompilationOptions.CryptoKeyFile },
                {"ManifestResources", cscArgs.ManifestResources },
                {"MetadataReferences", cscArgs.MetadataReferences }, // TODO: create metadata-only reference
                {"SourceFiles", cscArgs.SourceFiles },
                {"Win32Icon", cscArgs.Win32Icon },
                {"Win32Manifest", cscArgs.Win32Manifest },
                {"Win32ResourceFile", cscArgs.Win32ResourceFile }
            };
            verboseCache.AppendLine("Files:");
            var hashOfFiles = Hasher.Combine(from f in filesToHash select TopHashFilesWithLogging(f.Value, verboseCache, " " + f.Key));
            verboseCache.AppendFormat(" Total: {0}\n", new Guid(hashOfFiles).ToString());
            var totalHash = Hasher.Combine(hashOfFiles, hashOfCompilationModules);
            var hashKey = new Guid(totalHash);
            verboseCache.AppendFormat("Total: {0}\n", hashKey);

            var cacheDir = Path.Combine(_cacheDir, hashKey.ToString());
            Directory.CreateDirectory(cacheDir);
            File.WriteAllText(Path.Combine(cacheDir, "cacheSources.txt"), verboseCache.ToString());
            return hashKey;
        }

        private static byte[] TopHashFilesWithLogging(object o, StringBuilder sb, string tag)
        {
            var hash = HashFiles(o, sb, tag);
            sb.AppendFormat("{0} -> {1}\n\n", tag, new Guid(hash).ToString());
            return hash;
        }
        private static byte[] HashFiles(object o, StringBuilder sb, string tag)
        {
            //Debugger.Launch();
            if (o == null)
            {
                return new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };
            }
            if (o is string) // string is also IEnumerable, so must be checked before
            {
                var s = o as string;
                var hash = Hasher.ComputeHash(File.OpenRead(s));
                sb.AppendFormat("  {0} -> {1}\n", s, new Guid(hash).ToString());
                return hash;
            }
            if (o is IEnumerable)
            {
                var enumerable = (o as IEnumerable).Cast<object>();
                return Hasher.Combine(enumerable.Select(x => HashFiles(x, sb, tag)));
            }
            if (o is CommandLineSourceFile)
            {
                return HashFiles(((CommandLineSourceFile)o).Path, sb, tag);
            }
            if (o is CommandLineAnalyzerReference)
                return HashFiles(((CommandLineAnalyzerReference)o).FilePath, sb, tag);
            if (o is ResourceDescription)
                return HashFiles(ReadProperty<string>(o, "Microsoft.Cci.IFileReference.FileName"), sb, tag);
            if (o is CommandLineReference)
                return HashFiles(((CommandLineReference)o).Reference, sb, tag);

            throw new NotImplementedException(o.GetType().FullName);
        }

        private static T ReadProperty<T>(object o, string propName)
        {
            return (T)o.GetType().GetRuntimeProperties().Single(p=>p.Name ==propName).GetValue(o);
        }

        public bool TryEmit(Guid hashKey, string outputPath, string pdbPath, string documentationPath, out IEnumerable<Diagnostic> diagnostics)
        {
            diagnostics = new List<Diagnostic>();
            var cacheDir = Path.Combine(_cacheDir, hashKey.ToString());
            if (Directory.Exists(cacheDir) && File.Exists(Path.Combine(cacheDir, "cachedAt.txt")))
            {
                CopyFileFromDir(cacheDir, outputPath);
                CopyFileFromDir(cacheDir, pdbPath);
                CopyFileFromDir(cacheDir, documentationPath);

                // TODO: save / restore diagnostics
                return true;
            }
            return false;
        }

        public void Cache(Guid hashKey, string outputPath, string pdbPath, string documentationPath, IEnumerable<Diagnostic> diagnostics)
        {
            var cacheDir = Path.Combine(_cacheDir, hashKey.ToString());
            Directory.CreateDirectory(cacheDir);
            CopyFileToDir(outputPath, cacheDir);
            CopyFileToDir(pdbPath, cacheDir);
            CopyFileToDir(documentationPath, cacheDir);
            // TODO: save / restore diagnostics
            File.WriteAllText(Path.Combine(cacheDir, "cachedAt.txt"), DateTime.Now.ToString());
        }

        private void CopyFileToDir(string sourceFile, string destDir)
        {
            if (!String.IsNullOrWhiteSpace(sourceFile) && File.Exists(sourceFile))
            {
                var destFile = Path.Combine(destDir, Path.GetFileName(sourceFile));
                File.Copy(sourceFile, destFile, overwrite: true);
            }
        }

        private void CopyFileFromDir(string sourceDir, string destFile)
        {
            if (!String.IsNullOrWhiteSpace(destFile))
            {
                var sourceFile = Path.Combine(sourceDir, Path.GetFileName(destFile));
                File.Copy(sourceFile, destFile, overwrite: true);
            }
        }
    }

    public static class ArrayExt
    {
        public static T[] AsArray<T>(this IEnumerable<T> x)
        {
            if (x is T[])
                return x as T[];
            return x.ToArray();
        }
    }

    public static class Hasher
    {
        public static byte[] ComputeHash(string s)
        {
            using (var algo = MD5.Create())
                return algo.ComputeHash(Encoding.Unicode.GetBytes(s));
        }

        public static byte[] Combine(IEnumerable<byte[]> bytes)
        {
            return Combine(bytes.AsArray());
        }

        public static byte[] Combine(params byte[][] bytes)
        {
            if (bytes.Length == 0)
                return new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };
            if (bytes.Length == 1)
                return bytes.Single();
            using (var algo = MD5.Create())
            {
                var hash = new byte[algo.OutputBlockSize];
                algo.Initialize();
                for (int i = 0; i < bytes.Length; i++)
                {
                    var b = bytes[i];
                    if (i < bytes.Length - 1)
                        algo.TransformBlock(b, 0, b.Length, b, 0);
                    else
                        algo.TransformFinalBlock(b, 0, b.Length);
                }
                return algo.Hash;
            }
        }

        public static byte[] ComputeHash(byte[] bytes)
        {
            using (var algo = MD5.Create())
                return algo.ComputeHash(bytes);
        }

        public static byte[] ComputeHash(Stream stream)
        {
            using (var algo = MD5.Create())
                return algo.ComputeHash(stream);
        }
    }
}
