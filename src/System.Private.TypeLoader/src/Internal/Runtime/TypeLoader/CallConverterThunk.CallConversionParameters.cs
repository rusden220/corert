// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.


#if ARM
#define _TARGET_ARM_
#define CALLDESCR_ARGREGS                          // CallDescrWorker has ArgumentRegister parameter
#define CALLDESCR_FPARGREGS                        // CallDescrWorker has FloatArgumentRegisters parameter
#define CALLDESCR_FPARGREGSARERETURNREGS           // The return value floating point registers are the same as the argument registers
#define ENREGISTERED_RETURNTYPE_MAXSIZE
#define ENREGISTERED_RETURNTYPE_INTEGER_MAXSIZE
#define FEATURE_HFA
#elif ARM64
#define _TARGET_ARM64_
#define CALLDESCR_ARGREGS                          // CallDescrWorker has ArgumentRegister parameter
#define CALLDESCR_FPARGREGS                        // CallDescrWorker has FloatArgumentRegisters parameter
#define CALLDESCR_FPARGREGSARERETURNREGS           // The return value floating point registers are the same as the argument registers
#define ENREGISTERED_RETURNTYPE_MAXSIZE
#define ENREGISTERED_RETURNTYPE_INTEGER_MAXSIZE
#define ENREGISTERED_PARAMTYPE_MAXSIZE
#define FEATURE_HFA
#elif X86
#define _TARGET_X86_
#define ENREGISTERED_RETURNTYPE_MAXSIZE
#define ENREGISTERED_RETURNTYPE_INTEGER_MAXSIZE
#define CALLDESCR_ARGREGS                          // CallDescrWorker has ArgumentRegister parameter
#define CALLINGCONVENTION_CALLEE_POPS
#elif AMD64
#if UNIXAMD64
#define UNIX_AMD64_ABI
#define CALLDESCR_ARGREGS                          // CallDescrWorker has ArgumentRegister parameter
#else
#endif
#define CALLDESCR_FPARGREGS                        // CallDescrWorker has FloatArgumentRegisters parameter
#define _TARGET_AMD64_
#define CALLDESCR_FPARGREGSARERETURNREGS           // The return value floating point registers are the same as the argument registers
#define ENREGISTERED_RETURNTYPE_MAXSIZE
#define ENREGISTERED_RETURNTYPE_INTEGER_MAXSIZE
#define ENREGISTERED_PARAMTYPE_MAXSIZE
#else
#error Unknown architecture!
#endif

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Internal.Runtime.Augments;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using Internal.Runtime.CompilerServices;
using Internal.NativeFormat;
using Internal.TypeSystem;
using Internal.Runtime.CallConverter;

using ArgIterator = Internal.Runtime.CallConverter.ArgIterator;
using CallingConvention = Internal.Runtime.CallConverter.CallingConvention;

namespace Internal.Runtime.TypeLoader
{
    internal unsafe struct CallConversionParameters
    {
        internal class GCHandleContainer
        {
            internal GCHandle _thisPtrHandle;
            internal GCHandle _dynamicInvokeArgHandle;
            internal GCHandle _returnObjectHandle;

            internal GCHandleContainer()
            {
                // Allocations of pinned gc handles done only once during the lifetime of a thread.
                // An empty string is used as the initial pinned object reference.
                _thisPtrHandle = GCHandle.Alloc("", GCHandleType.Pinned);
                _dynamicInvokeArgHandle = GCHandle.Alloc("", GCHandleType.Pinned);
                _returnObjectHandle = GCHandle.Alloc("", GCHandleType.Pinned);
            }

            ~GCHandleContainer()
            {
                // Free all the pinned objects when the thread dies to avoid gchandle leaks
                _thisPtrHandle.Free();
                _dynamicInvokeArgHandle.Free();
                _returnObjectHandle.Free();
            }
        }

        private struct DelegateData
        {
            internal Delegate _delegateObject;
            internal object _firstParameter;
            internal object _helperObject;
            internal IntPtr _extraFunctionPointerOrData;
            internal IntPtr _functionPointer;

            // Computed fields
            internal object _boxedFirstParameter;
            internal object _multicastThisPointer;
            internal int _multicastTargetCount;
        }

        internal CallConversionInfo _conversionInfo;
        internal ArgIterator _callerArgs;
        internal ArgIterator _calleeArgs;
        internal byte* _callerTransitionBlock;
        internal IntPtr _invokeReturnValue;
        internal bool _copyReturnValue;
        internal object[] _dynamicInvokeParams;
        internal CallConverterThunk.DynamicInvokeByRefArgObjectWrapper[] _dynamicInvokeByRefObjectArgs;
        private IntPtr _instantiatingStubArgument;
        private IntPtr _functionPointerToCall;
        private DelegateData _delegateData;

        [ThreadStatic]
        internal static GCHandleContainer s_pinnedGCHandles;

        // Signature of the DynamicInvokeImpl method on delegates is always the same.
        private static ArgIteratorData s_delegateDynamicInvokeImplArgIteratorData = new ArgIteratorData(true, false, new TypeHandle[]{
                new TypeHandle(false, typeof(object).TypeHandle),
                new TypeHandle(false, typeof(IntPtr).TypeHandle),
                new TypeHandle(true, typeof(InvokeUtils.ArgSetupState).TypeHandle)
            }, new TypeHandle(false, typeof(object).TypeHandle));

        // Signature of all the InvokeRetXYZ reflection invoker stubs is always the same.
        private static ArgIteratorData s_reflectionDynamicInvokeImplArgIteratorData = new ArgIteratorData(false, false, new TypeHandle[]{
                new TypeHandle(false, typeof(object).TypeHandle),
                new TypeHandle(false, typeof(IntPtr).TypeHandle),
                new TypeHandle(true, typeof(InvokeUtils.ArgSetupState).TypeHandle),
                new TypeHandle(false, typeof(bool).TypeHandle)
            }, new TypeHandle(false, typeof(object).TypeHandle));

        internal CallConversionParameters(CallConversionInfo conversionInfo, IntPtr callerTransitionBlockParam)
        {
            // Make sure the thred static variable has been initialized for this thread
            s_pinnedGCHandles = s_pinnedGCHandles ?? new GCHandleContainer();

            _conversionInfo = conversionInfo;
            _callerTransitionBlock = (byte*)callerTransitionBlockParam.ToPointer();
            _functionPointerToCall = conversionInfo.TargetFunctionPointer;
            _instantiatingStubArgument = conversionInfo.InstantiatingStubArgument;
            _delegateData = default(DelegateData);
            _calleeArgs = default(ArgIterator);
            _invokeReturnValue = IntPtr.Zero;
            _copyReturnValue = true;
            _dynamicInvokeParams = null;
            _dynamicInvokeByRefObjectArgs = null;

            // 
            // Setup input argument iterator for the caller
            //
            ArgIteratorData callerIteratorData;

            if (conversionInfo.IsDelegateDynamicInvokeThunk)
                callerIteratorData = s_delegateDynamicInvokeImplArgIteratorData;
            else if (conversionInfo.IsReflectionDynamicInvokerThunk)
                callerIteratorData = s_reflectionDynamicInvokeImplArgIteratorData;
            else
                callerIteratorData = conversionInfo.ArgIteratorData;

            _callerArgs = new ArgIterator(callerIteratorData,
                                              callerIteratorData.HasThis() ?
                                                CallingConvention.ManagedInstance :
                                                CallingConvention.ManagedStatic,
                                              conversionInfo.CallerHasParamType,
                                              conversionInfo.CallerHasExtraParameterWhichIsFunctionTarget,
                                              conversionInfo.CallerForcedByRefData,
                                              false, false); // Setup input

            bool forceCalleeHasParamType = false;

            // If the callee MAY have a param type, we need to know before we create the callee arg iterator
            // To do this we need to actually load the target address and see if it has the generic method pointer
            // bit set.
            if (conversionInfo.CalleeMayHaveParamType)
            {
                ArgIterator callerArgsLookupTargetFunctionPointer = new ArgIterator(conversionInfo.ArgIteratorData,
                                                                                        conversionInfo.ArgIteratorData.HasThis() ?
                                                                                            CallingConvention.ManagedInstance :
                                                                                            CallingConvention.ManagedStatic,
                                                                                        conversionInfo.CallerHasParamType,
                                                                                        conversionInfo.CallerHasExtraParameterWhichIsFunctionTarget,
                                                                                        conversionInfo.CallerForcedByRefData,
                                                                                        false, false);

                // Find the last valid caller offset. That's the offset of the target function pointer.
                int ofsCallerValid = TransitionBlock.InvalidOffset;
                while (true)
                {
                    // Setup argument offsets.
                    int ofsCallerTemp = callerArgsLookupTargetFunctionPointer.GetNextOffset();

                    // Check to see if we've handled all the arguments that we are to pass to the callee. 
                    if (TransitionBlock.InvalidOffset == ofsCallerTemp)
                        break;

                    ofsCallerValid = ofsCallerTemp;
                }

                if (ofsCallerValid == TransitionBlock.InvalidOffset)
                    throw new InvalidProgramException();

                int stackSizeCaller = callerArgsLookupTargetFunctionPointer.GetArgSize();
                Debug.Assert(stackSizeCaller == IntPtr.Size);
                void* pSrc = _callerTransitionBlock + ofsCallerValid;
                IntPtr tempFunctionPointer = *((IntPtr*)pSrc);

                forceCalleeHasParamType = UpdateCalleeFunctionPointer(tempFunctionPointer);
            }

            // Retrieve target function pointer and instantiation argument for delegate thunks
            if (conversionInfo.IsDelegateThunk)
            {
                Debug.Assert(_callerArgs.HasThis() && !_conversionInfo.IsUnboxingThunk);

                IntPtr locationOfThisPointer = (IntPtr)(_callerTransitionBlock + ArgIterator.GetThisOffset());
                _delegateData._delegateObject = (Delegate)Unsafe.As<IntPtr, Object>(ref *(IntPtr*)locationOfThisPointer);
                Debug.Assert(_delegateData._delegateObject != null);

                RuntimeAugments.GetDelegateData(
                    _delegateData._delegateObject,
                    out _delegateData._firstParameter,
                    out _delegateData._helperObject,
                    out _delegateData._extraFunctionPointerOrData,
                    out _delegateData._functionPointer);

                if (conversionInfo.TargetDelegateFunctionIsExtraFunctionPointerOrDataField)
                {
                    if (conversionInfo.IsOpenInstanceDelegateThunk)
                    {
                        _delegateData._boxedFirstParameter = BoxedCallerFirstArgument;
                        Debug.Assert(_delegateData._boxedFirstParameter != null);
                        _callerArgs.Reset();

                        IntPtr resolvedTargetFunctionPointer = OpenMethodResolver.ResolveMethod(_delegateData._extraFunctionPointerOrData, _delegateData._boxedFirstParameter);
                        forceCalleeHasParamType = UpdateCalleeFunctionPointer(resolvedTargetFunctionPointer);
                    }
                    else
                    {
                        forceCalleeHasParamType = UpdateCalleeFunctionPointer(_delegateData._extraFunctionPointerOrData);
                    }
                }
                else if (conversionInfo.IsMulticastDelegate)
                {
                    _delegateData._multicastTargetCount = (int)_delegateData._extraFunctionPointerOrData;
                }
            }

            // 
            // Setup output argument iterator for the callee
            //
            _calleeArgs = new ArgIterator(conversionInfo.ArgIteratorData,
                                                (conversionInfo.ArgIteratorData.HasThis() && !conversionInfo.IsStaticDelegateThunk) ?
                                                    CallingConvention.ManagedInstance :
                                                    CallingConvention.ManagedStatic,
                                                forceCalleeHasParamType || conversionInfo.CalleeHasParamType,
                                                false,
                                                conversionInfo.CalleeForcedByRefData,
                                                conversionInfo.IsOpenInstanceDelegateThunk,
                                                conversionInfo.IsClosedStaticDelegate);

            // The function pointer, 'hasParamType', and 'hasThis' flags for the callee arg iterator need to be computed/read from the caller's 
            // input arguments in the case of a reflection invoker thunk (the target method pointer and 'hasThis' flags are
            // passed in as parameters from the caller, not loaded from a static method signature in native layout)
            if (conversionInfo.IsReflectionDynamicInvokerThunk)
                ComputeCalleeFlagsAndFunctionPointerForReflectionInvokeThunk();

#if CALLINGCONVENTION_CALLEE_POPS
            // Ensure that the count of bytes in the stack is available
            _callerArgs.CbStackPop();
#endif
        }

        private void ComputeCalleeFlagsAndFunctionPointerForReflectionInvokeThunk()
        {
            Debug.Assert(_conversionInfo.IsReflectionDynamicInvokerThunk);
            Debug.Assert(!_callerArgs.Equals(default(ArgIterator)) && !_calleeArgs.Equals(default(ArgIterator)));

            _callerArgs.GetNextOffset();     // Skip thisPtr

            {
                int ofsCaller = _callerArgs.GetNextOffset();     // methodToCall
                Debug.Assert(TransitionBlock.InvalidOffset != ofsCaller);

                void** pSrc = (void**)(_callerTransitionBlock + ofsCaller);

                IntPtr functionPointer = new IntPtr(*pSrc);

                bool forceCalleeHasParamType = UpdateCalleeFunctionPointer(functionPointer);
                _calleeArgs.SetHasParamTypeAndReset(forceCalleeHasParamType);
            }

            _callerArgs.GetNextOffset();     // Skip argSetupState

            // targetIsThisCall
            {
                int ofsCaller = _callerArgs.GetNextOffset();     // targetIsThisCall
                Debug.Assert(TransitionBlock.InvalidOffset != ofsCaller);

                bool* pSrc = (bool*)(_callerTransitionBlock + ofsCaller);

                bool targetIsThisCall = *pSrc;

                _calleeArgs.SetHasThisAndReset(targetIsThisCall);
            }

            _callerArgs.Reset();
        }

        internal void ResetPinnedObjects()
        {
            // Reset all pinned gchandles to an empty string.
            // Freeing of gchandles is done in the destructor of GCHandleContainer when the thread dies
            s_pinnedGCHandles._thisPtrHandle.Target = "";
            s_pinnedGCHandles._returnObjectHandle.Target = "";
            s_pinnedGCHandles._dynamicInvokeArgHandle.Target = "";
        }

        private bool UpdateCalleeFunctionPointer(IntPtr newFunctionPointer)
        {
            if (FunctionPointerOps.IsGenericMethodPointer(newFunctionPointer))
            {
                GenericMethodDescriptor* genericTarget = FunctionPointerOps.ConvertToGenericDescriptor(newFunctionPointer);
                _instantiatingStubArgument = genericTarget->InstantiationArgument;
                _functionPointerToCall = genericTarget->MethodFunctionPointer;
                return true;
            }
            else
            {
                _functionPointerToCall = newFunctionPointer;
                return false;
            }
        }

        internal void PrepareNextMulticastDelegateCall(int currentIndex)
        {
            if (!_conversionInfo.IsMulticastDelegate)
            {
                Environment.FailFast("Thunk is not a multicast delegate thunk!");
            }

            Debug.Assert(currentIndex < _delegateData._multicastTargetCount);

            Debug.Assert(!_delegateData.Equals(default(DelegateData)));
            Debug.Assert(_delegateData._helperObject is Delegate[]);
            Debug.Assert(_delegateData._multicastTargetCount <= ((Delegate[])_delegateData._helperObject).Length);

            Delegate[] delegateArray = (Delegate[])_delegateData._helperObject;
            Delegate currentDelegate = delegateArray[currentIndex];

            object helperObject;
            IntPtr extraFunctionPointerOrData;
            IntPtr functionPointer;
            RuntimeAugments.GetDelegateData(currentDelegate, out _delegateData._multicastThisPointer, out helperObject, out extraFunctionPointerOrData, out functionPointer);

            bool forceCalleeHasParamType = UpdateCalleeFunctionPointer(functionPointer);
            _calleeArgs.SetHasParamTypeAndReset(forceCalleeHasParamType);
            _callerArgs.Reset();
        }

        internal int MulticastDelegateCallCount
        {
            get
            {
                Debug.Assert(_conversionInfo.IsMulticastDelegate);
                return _delegateData._multicastTargetCount;
            }
        }

        private object BoxedCallerFirstArgument
        {
            // Get the first argument that will be passed to the callee (if available), and box it if it's a value type.
            // The first argument is an actual explicit argument passed to the callee... i.e. not a thispointer or instantiationStubArg
            // NOTE: This method advances the caller ArgIterator and does NOT reset it. It's up to whoever calls this method
            // to reset the caller ArgIterator if it needs to be reset
            get
            {
                int ofsCaller = _callerArgs.GetNextOffset();
                if (TransitionBlock.InvalidOffset == ofsCaller)
                    return null;

                byte* pSrc = _callerTransitionBlock + ofsCaller;

                TypeHandle thValueType;
                _callerArgs.GetArgType(out thValueType);

                if (thValueType.IsNull())
                {
                    Debug.Assert(_callerArgs.GetArgSize() == IntPtr.Size);
                    Debug.Assert(!_callerArgs.IsArgPassedByRef());
                    return Unsafe.As<IntPtr, Object>(ref *(IntPtr*)pSrc);
                }
                else
                {
                    RuntimeTypeHandle argEEType = thValueType.GetRuntimeTypeHandle();

                    if (_callerArgs.IsArgPassedByRef())
                    {
                        return RuntimeAugments.Box(argEEType, new IntPtr(*((void**)pSrc)));
                    }
                    else
                    {
                        return RuntimeAugments.Box(argEEType, new IntPtr(pSrc));
                    }
                }
            }
        }

        internal IntPtr ClosedStaticDelegateThisPointer
        {
            get
            {
                Debug.Assert(_conversionInfo.IsClosedStaticDelegate && !_delegateData.Equals(default(DelegateData)));
                Debug.Assert(_delegateData._helperObject != null);
                s_pinnedGCHandles._thisPtrHandle.Target = _delegateData._helperObject;
                return RuntimeAugments.GetRawAddrOfPinnedObject((IntPtr)s_pinnedGCHandles._thisPtrHandle);
            }
        }

        internal void* ThisPointer
        {
            get
            {
                if (_conversionInfo.IsStaticDelegateThunk)
                    return null;

                if (_callerArgs.HasThis() != _calleeArgs.HasThis())
                {
                    // Note: the _callerArgs for a reflection dynamic invoker thunk will never have a thisPtr
                    // because it's a static method. The _calleeArgs may have a thisPtr if the target method
                    // being called is an instance method.
                    if (!_conversionInfo.IsReflectionDynamicInvokerThunk)
                    {
                        // Whether or not a given signature has a this parameter is not allowed to change across this thunk.
                        Environment.FailFast("HasThis on signatures must match");
                    }
                }
                if (_calleeArgs.HasThis())
                {
                    void* thisPointer = null;

                    if (_conversionInfo.IsThisPointerInDelegateData || _conversionInfo.IsAnyDynamicInvokerThunk)
                    {
                        // Assert that we have extracted the delegate data
                        Debug.Assert(_conversionInfo.IsReflectionDynamicInvokerThunk || !_delegateData.Equals(default(DelegateData)));

                        if (_conversionInfo.IsAnyDynamicInvokerThunk)
                        {
                            // Resilience to multiple or out of order calls
                            _callerArgs.Reset();

                            int ofsCaller = _callerArgs.GetNextOffset();
                            Debug.Assert(TransitionBlock.InvalidOffset != ofsCaller);

                            void** pSrc = (void**)(_callerTransitionBlock + ofsCaller);

                            // No need to pin since the thisPtr is one of the arguments on the caller TB
                            return *pSrc;
                        }
                        if (_conversionInfo.IsOpenInstanceDelegateThunk)
                        {
                            // We should have already extracted and boxed the first parameter
                            Debug.Assert(_delegateData._boxedFirstParameter != null);
                            s_pinnedGCHandles._thisPtrHandle.Target = _delegateData._boxedFirstParameter;

                            // Note: We should advance the caller's ArgIterator since the first parameter is used as a thisPointer,
                            // not as an actual parameter passed to the callee.
                            // We do not need to advance the callee's ArgIterator because we already initialized it with a
                            // 'skipFirstArg' flag
                            _callerArgs.GetNextOffset();
                        }
                        else if (_conversionInfo.IsMulticastDelegate)
                        {
                            Debug.Assert(_delegateData._multicastThisPointer != null);
                            s_pinnedGCHandles._thisPtrHandle.Target = _delegateData._multicastThisPointer;
                        }
                        else
                        {
                            Debug.Assert(_delegateData._helperObject != null);
                            s_pinnedGCHandles._thisPtrHandle.Target = _delegateData._helperObject;
                        }

                        thisPointer = (void*)RuntimeAugments.GetRawAddrOfPinnedObject((IntPtr)s_pinnedGCHandles._thisPtrHandle);
                    }
                    else
                    {
                        thisPointer = *((void**)(_callerTransitionBlock + ArgIterator.GetThisOffset()));
                        if (_conversionInfo.IsUnboxingThunk)
                        {
                            thisPointer = (void*)(((IntPtr*)thisPointer) + 1);
                        }
                    }

                    return thisPointer;
                }
                return null;
            }
        }

        internal void* CallerReturnBuffer
        {
            get
            {
                // If the return buffer is treated the same way for both calling conventions
                if (_callerArgs.HasRetBuffArg() == _calleeArgs.HasRetBuffArg())
                {
                    // Do nothing, or copy the ret buf arg around
                    if (_callerArgs.HasRetBuffArg())
                    {
                        return *((void**)(_callerTransitionBlock + _callerArgs.GetRetBuffArgOffset()));
                    }
                }
                else
                {
                    // We'll need to create a return buffer, or assign into the return buffer when the actual call completes.
                    if (_calleeArgs.HasRetBuffArg())
                    {
                        TypeHandle thValueType;
                        bool forceByRefUnused;
                        void* callerRetBuffer = null;

                        if (_conversionInfo.IsAnyDynamicInvokerThunk)
                        {
                            // In the case of dynamic invoke thunks that use return buffers, we need to allocate a buffer for the return
                            // value, of the same type as the return value type handle in the callee's arguments.
                            Debug.Assert(!_callerArgs.HasRetBuffArg());

                            CorElementType returnType = _calleeArgs.GetReturnType(out thValueType, out forceByRefUnused);
                            RuntimeTypeHandle returnValueType = thValueType.IsNull() ? typeof(object).TypeHandle : thValueType.GetRuntimeTypeHandle();
                            s_pinnedGCHandles._returnObjectHandle.Target = RuntimeAugments.RawNewObject(returnValueType);

                            // The transition block has a space reserved for storing return buffer data. This is protected conservatively.
                            // Copy the address of the allocated object to the protected memory to be able to safely unpin it.
                            callerRetBuffer = _callerTransitionBlock + TransitionBlock.GetOffsetOfReturnValuesBlock();
                            *((void**)callerRetBuffer) = (void*)RuntimeAugments.GetRawAddrOfPinnedObject((IntPtr)s_pinnedGCHandles._returnObjectHandle);

                            // Unpin the allocated object (it's now protected in the caller's conservatively reported memory space)
                            s_pinnedGCHandles._returnObjectHandle.Target = "";

                            // Point the callerRetBuffer to the begining of the actual object's data (skipping the EETypePtr slot)
                            callerRetBuffer = (void*)(new IntPtr(*((void**)callerRetBuffer)) + IntPtr.Size);
                        }
                        else
                        {
                            // The transition block has a space reserved for storing return buffer data. This is protected conservatively
                            callerRetBuffer = _callerTransitionBlock + TransitionBlock.GetOffsetOfReturnValuesBlock();

                            // Make sure buffer is nulled out, and setup the return buffer location.
                            CorElementType returnType = _callerArgs.GetReturnType(out thValueType, out forceByRefUnused);
                            int returnSize = TypeHandle.GetElemSize(returnType, thValueType);
                            CallConverterThunk.memzeroPointerAligned((byte*)callerRetBuffer, returnSize);
                        }

                        Debug.Assert(callerRetBuffer != null);
                        return callerRetBuffer;
                    }
                }

                return null;
            }
        }

        internal void* VarArgSigCookie
        {
            get
            {
                if (_calleeArgs.IsVarArg() != _callerArgs.IsVarArg())
                {
                    // Whether or not a given signature has a this parameter is not allowed to change across this thunk.
                    Environment.FailFast("IsVarArg on signatures must match");
                }
                if (_calleeArgs.IsVarArg())
                {
                    return *((void**)(_callerTransitionBlock + _callerArgs.GetVASigCookieOffset()));
                }

                return null;
            }
        }

        internal IntPtr InstantiatingStubArgument
        {
            get
            {
                if (_calleeArgs.HasParamType() == _callerArgs.HasParamType())
                {
                    if (_calleeArgs.HasParamType())
                    {
                        return new IntPtr(*((void**)(_callerTransitionBlock + _callerArgs.GetParamTypeArgOffset())));
                    }
                }
                else if (_calleeArgs.HasParamType())
                {
                    Debug.Assert(_instantiatingStubArgument != IntPtr.Zero);
                    return _instantiatingStubArgument;
                }
                else
                {
                    // Whether or not a given signature has a this parameter is not allowed to change across this thunk.
                    Environment.FailFast("Uninstantiating thunks are not allowed");
                }

                return IntPtr.Zero;
            }
        }

        internal IntPtr FunctionPointerToCall
        {
            get
            {
                if (_conversionInfo.IsDelegateDynamicInvokeThunk)
                {
                    // Resilience to multiple or out of order calls
                    {
                        _callerArgs.Reset();
                        _callerArgs.GetNextOffset();     // thisPtr
                    }

                    int ofsCaller = _callerArgs.GetNextOffset();     // methodToCall
                    Debug.Assert(TransitionBlock.InvalidOffset != ofsCaller);

                    void** pSrc = (void**)(_callerTransitionBlock + ofsCaller);

                    return new IntPtr(*pSrc);
                }

                return _functionPointerToCall;
            }
        }

        internal IntPtr InvokeObjectArrayDelegate(object[] arguments)
        {
            if (!_conversionInfo.IsObjectArrayDelegateThunk)
                Environment.FailFast("Thunk is not an object array delegate thunk!");

            Debug.Assert(!_delegateData.Equals(default(DelegateData)));

            Func<object[], object> targetDelegate = _delegateData._helperObject as Func<object[], object>;
            Debug.Assert(targetDelegate != null);

            s_pinnedGCHandles._returnObjectHandle.Target = targetDelegate(arguments ?? Array.Empty<object>());

            return RuntimeAugments.GetRawAddrOfPinnedObject((IntPtr)s_pinnedGCHandles._returnObjectHandle);
        }

        internal IntPtr GetArgSetupStateDataPointer()
        {
            if (!_conversionInfo.IsAnyDynamicInvokerThunk)
                Environment.FailFast("Thunk is not a valid dynamic invoker thunk!");

            // Resilience to multiple or out of order calls
            {
                _callerArgs.Reset();
                _callerArgs.GetNextOffset();     // thisPtr
                _callerArgs.GetNextOffset();     // methodToCall
            }

            int ofsCaller = _callerArgs.GetNextOffset();     //argSetupState
            Debug.Assert(TransitionBlock.InvalidOffset != ofsCaller);

            void** pSrc = (void**)(_callerTransitionBlock + ofsCaller);

            // No need to pin since the argSetupState is one of the arguments on the caller TB
            return new IntPtr(*pSrc);
        }
    }
}
