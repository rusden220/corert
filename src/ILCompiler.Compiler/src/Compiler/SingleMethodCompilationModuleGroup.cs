﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Internal.TypeSystem;

namespace ILCompiler
{
    /// <summary>
    /// A compilation group that only contains a single method. Useful for development purposes when investigating
    /// code generation issues.
    /// </summary>
    public class SingleMethodCompilationModuleGroup : CompilationModuleGroup
    {
        private MethodDesc _method;

        public SingleMethodCompilationModuleGroup(MethodDesc method)
            : base(method.Context)
        {
            _method = method;
        }

        public override bool IsSingleFileCompilation
        {
            get
            {
                return false;
            }
        }

        public override bool ContainsMethod(MethodDesc method)
        {
            return method == _method;
        }

        public override bool ContainsType(TypeDesc type)
        {
            return false;
        }

        public override bool ShouldProduceFullVTable(TypeDesc type)
        {
            return false;
        }

        public override bool ShouldPromoteToFullType(TypeDesc type)
        {
            return false;
        }

        public override bool ShouldReferenceThroughImportTable(TypeDesc type)
        {
            return false;
        }

        public override bool CanHaveReferenceThroughImportTable
        {
            get
            {
                return false;
            }
        }
    }
}
