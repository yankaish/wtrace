﻿module LowLevelDesign.WTrace.Events.ETW.Registry

open System
open System.Collections.Generic
open System.IO
open System.Security.Principal
open Microsoft.Diagnostics.Tracing
open Microsoft.Diagnostics.Tracing.Parsers.Kernel
open LowLevelDesign.WTrace.Events
open LowLevelDesign.WTrace
open LowLevelDesign.WTrace.WinApi

type private RegistryHandlerState = {
    HandlerId : int32
    Broadcast : EventBroadcast
    // a state to keep a map of key handles (KCB) to actual key names
    KeyHandleToName : Dictionary<uint64, string>
}

let metadata = [|
    EventProvider (kernelProviderId, "Kernel")
    EventTask (kernelProviderId, 4, "Registry")
    EventOpcode (kernelProviderId, 4, 10, "Create")
    EventOpcode (kernelProviderId, 4, 11, "Open")
    EventOpcode (kernelProviderId, 4, 12, "Delete")
    EventOpcode (kernelProviderId, 4, 13, "Query")
    EventOpcode (kernelProviderId, 4, 14, "SetValue")
    EventOpcode (kernelProviderId, 4, 15, "DeleteValue")
    EventOpcode (kernelProviderId, 4, 16, "QueryValue")
    EventOpcode (kernelProviderId, 4, 17, "EnumerateKey")
    EventOpcode (kernelProviderId, 4, 18, "EnumerateValueKey")
    EventOpcode (kernelProviderId, 4, 19, "QueryMultipleValue")
    EventOpcode (kernelProviderId, 4, 20, "SetInformation")
    EventOpcode (kernelProviderId, 4, 21, "Flush")
    EventOpcode (kernelProviderId, 4, 22, "KCBCreate")
    EventOpcode (kernelProviderId, 4, 23, "KCBDelete")
    EventOpcode (kernelProviderId, 4, 24, "KCBRundownBegin")
    EventOpcode (kernelProviderId, 4, 25, "KCBRundowEnd")
    EventOpcode (kernelProviderId, 4, 26, "Virtualize")
    EventOpcode (kernelProviderId, 4, 27, "Close")
|]

[<AutoOpen>]
module private H =
    let currentUserSid = WindowsIdentity.GetCurrent().User.ToString();

    let knownRegistryNames = [|
        (sprintf "\\Registry\\User\\%s_classes" currentUserSid, "HKCR")
        (sprintf "\\Registry\\User\\%s" currentUserSid, "HKCU")
        ("\\Registry\\User", "HKU")
        ("\\Registry\\Machine", "HKLM")
    |]

    let noFields = Array.empty<TraceEventField>

    let abbreviate (keyName : string) =
        let abbr = knownRegistryNames |> Array.tryFind (fun (n, _) -> keyName.StartsWith(n, StringComparison.OrdinalIgnoreCase))
        match abbr with
        | Some (n, a) -> a + (keyName.Substring(n.Length))
        | None -> keyName

    let handleKCBCreateEvent state (ev : RegistryTraceData) =
        state.KeyHandleToName.[ev.KeyHandle] <- abbreviate ev.KeyName

    let handleKCBDeleteEvent state (ev : RegistryTraceData) =
        state.KeyHandleToName.Remove(ev.KeyHandle) |> ignore

    let handleRegistryEvent id ts state (ev : RegistryTraceData) =
        let path =
            if ev.KeyHandle = 0UL then
                abbreviate ev.KeyName
            else
                let baseKeyName = 
                    match state.KeyHandleToName.TryGetValue(ev.KeyHandle) with
                    | (true, name) -> name
                    | (false, _) -> sprintf "<0x%X>" ev.KeyHandle
                Path.Combine(baseKeyName, ev.KeyName)

        let ev = toEvent state.HandlerId ev id ts path "" ev.Status
        state.Broadcast.publishTraceEvent (TraceEventWithFields (ev, noFields))

    let subscribe (source : TraceEventSource, isRundown, idgen, tsadj, state : obj) =
        let state = state :?> RegistryHandlerState
        let handleEvent h = Action<_>(handleEvent idgen tsadj state h)
        let handle h = Action<_>(h state)
        if isRundown then
            source.Kernel.add_RegistryKCBRundownBegin(handle handleKCBCreateEvent)
            source.Kernel.add_RegistryKCBRundownEnd(handle handleKCBCreateEvent)

            publishHandlerMetadata metadata state.Broadcast.publishMetaEvent
        else
            source.Kernel.add_RegistryKCBCreate(handle handleKCBCreateEvent)
            source.Kernel.add_RegistryKCBDelete(handle handleKCBDeleteEvent)

            source.Kernel.add_RegistryCreate(handleEvent handleRegistryEvent)
            source.Kernel.add_RegistryOpen(handleEvent handleRegistryEvent)
            source.Kernel.add_RegistryClose(handleEvent handleRegistryEvent)
            source.Kernel.add_RegistryFlush(handleEvent handleRegistryEvent)
            source.Kernel.add_RegistryEnumerateKey(handleEvent handleRegistryEvent)
            source.Kernel.add_RegistryQuery(handleEvent handleRegistryEvent)
            source.Kernel.add_RegistrySetInformation(handleEvent handleRegistryEvent)
            source.Kernel.add_RegistryVirtualize(handleEvent handleRegistryEvent)
            source.Kernel.add_RegistryDelete(handleEvent handleRegistryEvent)
            
            source.Kernel.add_RegistryEnumerateValueKey(handleEvent handleRegistryEvent)
            source.Kernel.add_RegistryQueryValue(handleEvent handleRegistryEvent)
            source.Kernel.add_RegistryQueryMultipleValue(handleEvent handleRegistryEvent)
            source.Kernel.add_RegistrySetValue(handleEvent handleRegistryEvent)
            source.Kernel.add_RegistryDeleteValue(handleEvent handleRegistryEvent)


let createEtwHandler () =
    {
        KernelFlags = NtKeywords.Registry
        KernelStackFlags = NtKeywords.Registry
        KernelRundownFlags = NtKeywords.Registry
        Providers = Array.empty<EtwEventProvider>
        Initialize = 
            fun (id, broadcast) -> ({
                HandlerId = id
                Broadcast = broadcast
                KeyHandleToName = Dictionary<uint64, string>()
            } :> obj)
        Subscribe = subscribe
    }

