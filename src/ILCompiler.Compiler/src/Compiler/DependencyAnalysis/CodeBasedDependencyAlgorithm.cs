// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;

using Internal.Text;
using Internal.TypeSystem;
using ILCompiler.DependencyAnalysisFramework;
using ILCompiler.DependencyAnalysis;

using DependencyList=ILCompiler.DependencyAnalysisFramework.DependencyNodeCore<ILCompiler.DependencyAnalysis.NodeFactory>.DependencyList;
using DependencyListEntry=ILCompiler.DependencyAnalysisFramework.DependencyNodeCore<ILCompiler.DependencyAnalysis.NodeFactory>.DependencyListEntry;

namespace ILCompiler.DependencyAnalysis
{
    public static class CodeBasedDependencyAlgorithm
    {
        public static void AddDependenciesDueToMethodCodePresence(ref DependencyList dependencies, NodeFactory factory, MethodDesc method)
        {
            // Reflection invoke stub handling is here because in the current reflection model we reflection-enable
            // all methods that are compiled. Ideally the list of reflection enabled methods should be known before
            // we even start the compilation process (with the invocation stubs being compilation roots like any other).
            // The existing model has it's problems: e.g. the invocability of the method depends on inliner decisions.
            if (factory.MetadataManager.IsReflectionInvokable(method))
            {
                if (dependencies == null)
                    dependencies = new DependencyList();

                if (factory.MetadataManager.HasReflectionInvokeStubForInvokableMethod(method) && !method.IsCanonicalMethod(CanonicalFormKind.Any) /* Shared generics handled in the shadow concrete method node */)
                {
                    MethodDesc invokeStub = factory.MetadataManager.GetReflectionInvokeStub(method);
                    MethodDesc canonInvokeStub = invokeStub.GetCanonMethodTarget(CanonicalFormKind.Specific);
                    if (invokeStub != canonInvokeStub)
                        dependencies.Add(new DependencyListEntry(factory.FatFunctionPointer(invokeStub), "Reflection invoke"));
                    else
                        dependencies.Add(new DependencyListEntry(factory.MethodEntrypoint(invokeStub), "Reflection invoke"));
                }

                bool skipUnboxingStubDependency = false;
                if (factory.Target.Abi == TargetAbi.ProjectN)
                {
                    // ProjectN compilation currently computes the presence of these stubs independent from dependency analysis here
                    // TODO: fix that issue and remove this odd treatment of unboxing stubs
                    if (!method.HasInstantiation && method.OwningType.IsValueType && method.OwningType.IsCanonicalSubtype(CanonicalFormKind.Any) && !method.Signature.IsStatic)
                        skipUnboxingStubDependency = true;
                }

                if (method.OwningType.IsValueType && !method.Signature.IsStatic && !skipUnboxingStubDependency)
                    dependencies.Add(new DependencyListEntry(factory.MethodEntrypoint(method, unboxingStub: true), "Reflection unboxing stub"));

                dependencies.AddRange(ReflectionVirtualInvokeMapNode.GetVirtualInvokeMapDependencies(factory, method));
            }

            if (method.HasInstantiation)
            {
                var exactMethodInstantiationDependencies = ExactMethodInstantiationsNode.GetExactMethodInstantiationDependenciesForMethod(factory, method);
                if (exactMethodInstantiationDependencies != null)
                {
                    dependencies = dependencies ?? new DependencyList();
                    dependencies.AddRange(exactMethodInstantiationDependencies);
                }

                if (method.IsVirtual)
                {
                    // Generic virtual methods dependency tracking
                    dependencies = dependencies ?? new DependencyList();
                    dependencies.Add(new DependencyListEntry(factory.GVMDependencies(method), "GVM Dependencies Support"));
                }

                var templateMethodDependencies = GenericMethodsTemplateMap.GetTemplateMethodDependencies(factory, method);
                if (templateMethodDependencies != null)
                {
                    dependencies = dependencies ?? new DependencyList();
                    dependencies.AddRange(templateMethodDependencies);
                }
            }
            else
            {
                TypeDesc owningTemplateType = method.OwningType;

                // Unboxing and Instantiating stubs use a different type as their template
                if (factory.TypeSystemContext.IsSpecialUnboxingThunk(method))
                    owningTemplateType = factory.TypeSystemContext.GetTargetOfSpecialUnboxingThunk(method).OwningType;

                var templateTypeDepedencies = GenericTypesTemplateMap.GetTemplateTypeDependencies(factory, owningTemplateType);
                if (templateTypeDepedencies != null)
                {
                    dependencies = dependencies ?? new DependencyList();
                    dependencies.AddRange(templateTypeDepedencies);
                }
            }
        }
    }
}