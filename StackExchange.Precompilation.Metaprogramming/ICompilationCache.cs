using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace StackExchange.Precompilation
{
    public interface ICompilationCache
    {
        Guid CalculateHash(string[] commandLine, CSharpCommandLineArguments cscArgs, List<ICompileModule> compilationModules);
        bool TryEmit(Guid hashKey, string outputPath, string pdbPath, string documentationPath, out IEnumerable<Diagnostic> diagnostics);
        void Cache(Guid hashKey, string outputPath, string pdbPath, string documentationPath, IEnumerable<Diagnostic> diagnostics);
    }
}
