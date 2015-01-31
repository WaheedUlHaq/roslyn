// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Scripting.Emit;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.Scripting
{
    /// <summary>
    /// Represents a runtime execution context for C# scripts.
    /// </summary>
    internal class ScriptBuilder
    {
        /// <summary>
        /// Unique prefix for generated uncollectible assemblies.
        /// </summary>
        /// <remarks>
        /// The full names of uncollectible assemblies generated by this context must be unique,
        /// so that we can resolve references among them. Note that CLR can load two different assemblies of the very 
        /// identity into the same load context.
        /// 
        /// We are using a certain naming scheme for the generated assemblies (a fixed name prefix followed by a number). 
        /// If we allowed the compiled code to add references that match this exact pattern it migth happen that 
        /// the user supplied reference identity conflicts with the identity we use for our generated assemblies and 
        /// the AppDomain assembly resolve event won't be able to correctly identify the target assembly.
        /// 
        /// To avoid this problem we use a prefix for assemblies we generate that is unlikely to conflict with user specified references.
        /// We also check that no user provided references are allowed to be used in the compiled code and report an error ("reserved assembly name").
        /// </remarks>
        private static readonly string s_globalAssemblyNamePrefix;
        private static int s_engineIdDispenser;
        private int _submissionIdDispenser = -1;
        private readonly string _assemblyNamePrefix;

        // dynamic code storage
        //
        // TODO (tomat): Dynamic assembly allocation strategy. A dynamic assembly is a unit of
        // collection. We need to find a balance between creating a new assembly per execution
        // (potentially shorter code lifetime) and reusing a single assembly for all executions
        // (less overhead per execution).

        private readonly UncollectibleCodeManager _uncollectibleCodeManager;
        private readonly CollectibleCodeManager _collectibleCodeManager;

        #region Testing and Debugging

        private const string CollectibleModuleFileName = "CollectibleModule.dll";
        private const string UncollectibleModuleFileName = "UncollectibleModule.dll";

        // Setting this flag will add DebuggableAttribute on the emitted code that disables JIT optimizations.
        // With optimizations disabled JIT will verify the method before it compiles it so we can easily 
        // discover incorrect code.
        internal static bool DisableJitOptimizations;

#if DEBUG
        // Flags to make debugging of emitted code easier.

        // Enables saving the dynamic assemblies to disk. Must be called before any code is compiled. 
        internal static bool EnableAssemblySave;

        // Helps debugging issues in emitted code. If set the next call to Execute/Compile will save the dynamic assemblies to disk.
        internal static bool SaveCompiledAssemblies;
#endif

        #endregion

        static ScriptBuilder()
        {
            s_globalAssemblyNamePrefix = "\u211B*" + Guid.NewGuid().ToString() + "-";
        }

        public ScriptBuilder(AssemblyLoader assemblyLoader = null)
        {
            if (assemblyLoader == null)
            {
                assemblyLoader = new InteractiveAssemblyLoader();
            }

            _assemblyNamePrefix = s_globalAssemblyNamePrefix + "#" + Interlocked.Increment(ref s_engineIdDispenser);
            _collectibleCodeManager = new CollectibleCodeManager(assemblyLoader, _assemblyNamePrefix);
            _uncollectibleCodeManager = new UncollectibleCodeManager(assemblyLoader, _assemblyNamePrefix);
        }

        public AssemblyLoader AssemblyLoader
        {
            get { return _collectibleCodeManager.assemblyLoader; }
        }

        internal string AssemblyNamePrefix
        {
            get { return _assemblyNamePrefix; }
        }

        internal static bool IsReservedAssemblyName(AssemblyIdentity identity)
        {
            return identity.Name.StartsWith(s_globalAssemblyNamePrefix);
        }

        public int GenerateSubmissionId(out string assemblyName, out string typeName)
        {
            int id = Interlocked.Increment(ref _submissionIdDispenser);
            assemblyName = _assemblyNamePrefix + id;
            typeName = "Submission#" + id;
            return id;
        }

        /// <summary>
        /// Builds a delegate that will execute just this scripts code.
        /// </summary>
        public Func<object[], object> Build(
            Script script,
            DiagnosticBag diagnostics,
            CancellationToken cancellationToken)
        {
            var compilation = script.GetCompilation();
            var options = script.Options;

            DiagnosticBag emitDiagnostics = DiagnosticBag.GetInstance();
            byte[] compiledAssemblyImage;
            MethodInfo entryPoint;

            bool success = compilation.Emit(
                 GetOrCreateDynamicModule(options.IsCollectible),
                assemblyLoader: GetAssemblyLoader(options.IsCollectible),
                assemblySymbolMapper: symbol => MapAssemblySymbol(symbol, options.IsCollectible),
                recoverOnError: true,
                diagnostics: emitDiagnostics,
                cancellationToken: cancellationToken,
                entryPoint: out entryPoint,
                compiledAssemblyImage: out compiledAssemblyImage
             );

            if (diagnostics != null)
            {
                diagnostics.AddRange(emitDiagnostics);
            }

            bool hadEmitErrors = emitDiagnostics.HasAnyErrors();
            emitDiagnostics.Free();

            // emit can fail due to compilation errors or because there is nothing to emit:
            if (!success)
            {
                return null;
            }

            Debug.Assert(entryPoint != null);

            if (compiledAssemblyImage != null)
            {
                // Ref.Emit wasn't able to emit the assembly
                _uncollectibleCodeManager.AddFallBackAssembly(entryPoint.DeclaringType.Assembly);
            }
#if DEBUG
            if (SaveCompiledAssemblies)
            {
                _uncollectibleCodeManager.Save(UncollectibleModuleFileName);
                _collectibleCodeManager.Save(CollectibleModuleFileName);
            }
#endif

            return (Func<object[], object>)Delegate.CreateDelegate(typeof(Func<object[], object>), entryPoint);
        }

        internal ModuleBuilder GetOrCreateDynamicModule(bool collectible)
        {
            if (collectible)
            {
                return _collectibleCodeManager.GetOrCreateDynamicModule();
            }
            else
            {
                return _uncollectibleCodeManager.GetOrCreateDynamicModule();
            }
        }

        private static ModuleBuilder CreateDynamicModule(AssemblyBuilderAccess access, AssemblyIdentity name, string fileName)
        {
            var assemblyBuilder = AppDomain.CurrentDomain.DefineDynamicAssembly(name.ToAssemblyName(), access);

            if (DisableJitOptimizations)
            {
                assemblyBuilder.SetCustomAttribute(new CustomAttributeBuilder(
                    typeof(DebuggableAttribute).GetConstructor(new[] { typeof(DebuggableAttribute.DebuggingModes) }),
                    new object[] { DebuggableAttribute.DebuggingModes.Default | DebuggableAttribute.DebuggingModes.DisableOptimizations }));
            }

            const string moduleName = "InteractiveModule";

            if (access == AssemblyBuilderAccess.RunAndSave)
            {
                return assemblyBuilder.DefineDynamicModule(moduleName, fileName, emitSymbolInfo: false);
            }
            else
            {
                return assemblyBuilder.DefineDynamicModule(moduleName, emitSymbolInfo: false);
            }
        }

        /// <summary>
        /// Maps given assembly symbol to an assembly ref.
        /// </summary>
        /// <remarks>
        /// The compiler represents every submission by a compilation instance for which it creates a distinct source assembly symbol.
        /// However multiple submissions might compile into a single dynamic assembly and so we need to map the corresponding assembly symbols to 
        /// the name of the dynamic assembly.
        /// </remarks>
        internal AssemblyIdentity MapAssemblySymbol(IAssemblySymbol symbol, bool collectible)
        {
            if (symbol.IsInteractive)
            {
                if (collectible)
                {
                    // collectible assemblies can't reference other generated assemblies
                    throw ExceptionUtilities.Unreachable;
                }
                else if (!_uncollectibleCodeManager.ContainsAssembly(symbol.Identity.Name))
                {
                    // uncollectible assemblies can reference uncollectible dynamic or uncollectible CCI generated assemblies:
                    return _uncollectibleCodeManager.dynamicAssemblyName;
                }
            }

            return symbol.Identity;
        }

        internal AssemblyLoader GetAssemblyLoader(bool collectible)
        {
            return collectible ? (AssemblyLoader)_collectibleCodeManager : _uncollectibleCodeManager;
        }

        // TODO (tomat): the code managers can be improved - common base class, less locking, etc.

        private sealed class CollectibleCodeManager : AssemblyLoader
        {
            internal readonly AssemblyLoader assemblyLoader;
            private readonly AssemblyIdentity _dynamicAssemblyName;

            // lock(this) on access
            internal ModuleBuilder dynamicModule;

            public CollectibleCodeManager(AssemblyLoader assemblyLoader, string assemblyNamePrefix)
            {
                this.assemblyLoader = assemblyLoader;
                _dynamicAssemblyName = new AssemblyIdentity(name: assemblyNamePrefix + "CD");

                AppDomain.CurrentDomain.AssemblyResolve += new ResolveEventHandler(Resolve);
            }

            internal ModuleBuilder GetOrCreateDynamicModule()
            {
                if (dynamicModule == null)
                {
                    lock (this)
                    {
                        if (dynamicModule == null)
                        {
                            dynamicModule = CreateDynamicModule(
#if DEBUG
                                EnableAssemblySave ? AssemblyBuilderAccess.RunAndSave :
#endif
                                AssemblyBuilderAccess.RunAndCollect, _dynamicAssemblyName, CollectibleModuleFileName);
                        }
                    }
                }

                return dynamicModule;
            }

            internal void Save(string fileName)
            {
                if (dynamicModule != null)
                {
                    ((AssemblyBuilder)dynamicModule.Assembly).Save(fileName);
                }
            }

            private Assembly Resolve(object sender, ResolveEventArgs args)
            {
                if (args.Name != _dynamicAssemblyName.GetDisplayName())
                {
                    return null;
                }

                lock (this)
                {
                    return (dynamicModule != null) ? dynamicModule.Assembly : null;
                }
            }

            public override Assembly Load(AssemblyIdentity identity, string location = null)
            {
                if (dynamicModule != null && identity.Name == _dynamicAssemblyName.Name)
                {
                    return dynamicModule.Assembly;
                }

                return assemblyLoader.Load(identity, location);
            }
        }

        /// <summary>
        /// Manages uncollectible assemblies and resolves assembly references baked into CCI generated metadata. 
        /// The resolution is triggered by the CLR Type Loader.
        /// </summary>
        private sealed class UncollectibleCodeManager : AssemblyLoader
        {
            private readonly AssemblyLoader _assemblyLoader;
            private readonly string _assemblyNamePrefix;
            internal readonly AssemblyIdentity dynamicAssemblyName;

            // lock(this) on access
            private ModuleBuilder _dynamicModule;      // primary uncollectible assembly
            private HashSet<Assembly> _fallBackAssemblies; // additional uncollectible assemblies created due to a Ref.Emit falling back to CCI
            private Dictionary<string, Assembly> _mapping; // { simple name -> fall-back assembly }

            internal UncollectibleCodeManager(AssemblyLoader assemblyLoader, string assemblyNamePrefix)
            {
                _assemblyLoader = assemblyLoader;
                _assemblyNamePrefix = assemblyNamePrefix;
                this.dynamicAssemblyName = new AssemblyIdentity(name: assemblyNamePrefix + "UD");

                AppDomain.CurrentDomain.AssemblyResolve += new ResolveEventHandler(Resolve);
            }

            internal ModuleBuilder GetOrCreateDynamicModule()
            {
                if (_dynamicModule == null)
                {
                    lock (this)
                    {
                        if (_dynamicModule == null)
                        {
                            _dynamicModule = CreateDynamicModule(
#if DEBUG
                                EnableAssemblySave ? AssemblyBuilderAccess.RunAndSave :
#endif
                                AssemblyBuilderAccess.Run, dynamicAssemblyName, UncollectibleModuleFileName);
                        }
                    }
                }

                return _dynamicModule;
            }

            internal void Save(string fileName)
            {
                if (_dynamicModule != null)
                {
                    ((AssemblyBuilder)_dynamicModule.Assembly).Save(fileName);
                }
            }

            internal void AddFallBackAssembly(Assembly assembly)
            {
                lock (this)
                {
                    if (_fallBackAssemblies == null)
                    {
                        Debug.Assert(_mapping == null);
                        _fallBackAssemblies = new HashSet<Assembly>();
                        _mapping = new Dictionary<string, Assembly>();
                    }

                    _fallBackAssemblies.Add(assembly);
                    _mapping[assembly.GetName().Name] = assembly;
                }
            }

            internal bool ContainsAssembly(string simpleName)
            {
                if (_mapping == null)
                {
                    return false;
                }

                lock (this)
                {
                    return _mapping.ContainsKey(simpleName);
                }
            }

            private Assembly Resolve(object sender, ResolveEventArgs args)
            {
                if (!args.Name.StartsWith(_assemblyNamePrefix))
                {
                    return null;
                }

                lock (this)
                {
                    if (args.Name == dynamicAssemblyName.GetDisplayName())
                    {
                        return _dynamicModule != null ? _dynamicModule.Assembly : null;
                    }

                    if (_dynamicModule != null && _dynamicModule.Assembly == args.RequestingAssembly ||
                        _fallBackAssemblies != null && _fallBackAssemblies.Contains(args.RequestingAssembly))
                    {
                        int comma = args.Name.IndexOf(',');
                        return ResolveNoLock(args.Name.Substring(0, (comma != -1) ? comma : args.Name.Length));
                    }
                }

                return null;
            }

            private Assembly Resolve(string simpleName)
            {
                lock (this)
                {
                    return ResolveNoLock(simpleName);
                }
            }

            private Assembly ResolveNoLock(string simpleName)
            {
                if (_dynamicModule != null && simpleName == dynamicAssemblyName.Name)
                {
                    return _dynamicModule.Assembly;
                }

                Assembly assembly;
                if (_mapping != null && _mapping.TryGetValue(simpleName, out assembly))
                {
                    return assembly;
                }

                return null;
            }

            public override Assembly Load(AssemblyIdentity identity, string location = null)
            {
                return Resolve(identity.Name) ?? _assemblyLoader.Load(identity, location);
            }
        }
    }
}