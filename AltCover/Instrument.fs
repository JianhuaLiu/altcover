﻿// Based upon C# code by Sergiy Sakharov (sakharov@gmail.com)
// http://code.google.com/p/dot-net-coverage/source/browse/trunk/Coverage/Instrument/CounterAssemblyBuilder.cs
// http://code.google.com/p/dot-net-coverage/source/browse/trunk/Coverage/Instrument/InstrumentorVisitor.cs

namespace AltCover

open System
open System.Collections.Generic
open System.Diagnostics.CodeAnalysis
open System.IO
open System.Reflection
open System.Resources

open AltCover.Augment
open Mono.Cecil
open Mono.Cecil.Cil
open Mono.Cecil.Rocks
open Newtonsoft.Json.Linq

/// <summary>
/// Module to handle instrumentation visitor
/// </summary>
module Instrument =

  /// <summary>
  /// State object passed from visit to visit
  /// </summary>
  [<ExcludeFromCodeCoverage>]
  type internal Context = { InstrumentedAssemblies : string list
                            RenameTable : Dictionary<String, String>
                            ModuleId : Guid
                            RecordingAssembly : AssemblyDefinition
                            RecordingMethod : MethodDefinition // initialised once
                            RecordingMethodRef : MethodReference // updated each module
                            MethodBody : MethodBody
                            MethodWorker : ILProcessor } // to save fetching repeatedly
  with static member Build assemblies =
                    { InstrumentedAssemblies = assemblies
                      RenameTable = null
                      ModuleId = Guid.Empty
                      RecordingAssembly = null
                      RecordingMethod = null
                      RecordingMethodRef = null
                      MethodBody = null
                      MethodWorker = null }

  // Can't hard-code what with .net-core and .net-core tests as well as classic .net
  // all giving this a different namespace
  let private resource = Assembly.GetExecutingAssembly().GetManifestResourceNames()
                         |> Seq.map (fun s -> s.Substring(0, s.Length - 10)) // trim ".resources"
                         |> Seq.find (fun n -> n.EndsWith(".JSONFragments", StringComparison.Ordinal))
  let private resources = ResourceManager(resource , Assembly.GetExecutingAssembly())
  let version = typeof<AltCover.Recorder.Tracer>.Assembly.GetName().Version.ToString()

  let monoRuntime = "Mono.Runtime" |> Type.GetType |> isNull |> not
#if NETCOREAPP2_0
  let dependencies = (resources.GetString "netcoreDependencies").Replace("version",
                                                                          version)
  let runtime = (resources.GetString "netcoreRuntime").Replace("AltCover.Recorder.g/version",
                                                               "AltCover.Recorder.g/" + version)
  let newLibraries = (resources.GetString "netcoreLibraries").Replace("AltCover.Recorder.g/version",
                                                                      "AltCover.Recorder.g/" + version)
#else
  let dependencies = (resources.GetString "frameworkDependencies").Replace("version",
                                                                            version)
  let runtime = (resources.GetString "frameworkRuntime").Replace("AltCover.Recorder.g/version",
                                                                  "AltCover.Recorder.g/" + version)
  let newLibraries = (resources.GetString "frameworkLibraries").Replace("AltCover.Recorder.g/version",
                                                                          "AltCover.Recorder.g/" + version)
#endif

  /// <summary>
  /// Workround for not being able to take typeof<SomeModule> even across
  /// assembly boundaries -- start with a pure type then iterate to the module
  /// </summary>
  /// <returns>A representation of the type used to record all coverage visits.</returns>
  let internal RecorderInstanceType () =
    let trace  = typeof<AltCover.Recorder.Tracer>
    trace.Assembly.GetExportedTypes()
    |> Seq.find (fun (t:Type) -> t.FullName = "AltCover.Recorder.Instance")

    /// <summary>
    /// Locate the method that must be called to register a code point for coverage visit.
    /// </summary>
    /// <param name="assembly">The assembly containing the recorder method</param>
    /// <returns>A representation of the method to call to signal a coverage visit.</returns>
  let internal RecordingMethod (recordingAssembly : AssemblyDefinition) =
    let other = RecorderInstanceType()
    let token = other.GetMethod("Visit").MetadataToken
    recordingAssembly.MainModule.LookupToken(token) :?> MethodDefinition

  /// <summary>
  /// Applies a new key to an assembly name
  /// </summary>
  /// <param name="assemblyName">The name to update</param>
  /// <param name="key">The possibly empty key to use</param>
  let internal UpdateStrongNaming (assemblyName:AssemblyNameDefinition) (key:StrongNameKeyPair option) =
#if NETCOREAPP2_0
    ()
#else
    match key with
    | None -> assemblyName.HasPublicKey <- false
              assemblyName.PublicKey <- null
              assemblyName.PublicKeyToken <- null
    | Some key' -> assemblyName.HasPublicKey <- true
                   assemblyName.PublicKey <- key'.PublicKey // sets token implicitly
#endif

  /// <summary>
  /// Locate the key, if any, which was used to name this assembly.
  /// </summary>
  /// <param name="name">The name of the assembly</param>
  /// <returns>A key, if we have a match.</returns>
  let internal KnownKey (name:AssemblyNameDefinition) =
      if not name.HasPublicKey then
        None
      else
        let index = KeyStore.ArrayToIndex name.PublicKey
        match Visitor.keys.TryGetValue(index) with
        | (false, _ ) -> None
        | (_, record) -> Some record.Pair

  /// <summary>
  /// Locate the key, if any, which was used to name this assembly.
  /// </summary>
  /// <param name="name">The name of the assembly</param>
  /// <returns>A key, if we have a match.</returns>
  let internal KnownToken (name:AssemblyNameReference) =
    let pktoken = name.PublicKeyToken
    if pktoken.Length <> 8  then
        None
    else
        let index = KeyStore.TokenAsULong pktoken
        match Visitor.keys.TryGetValue(index) with
        | (false, _ ) -> None
        | (_, record) -> Some record

  // This trivial extraction appeases Gendarme
  let private extractName (assembly: AssemblyDefinition) =
     assembly.Name.Name

  /// <summary>
  /// Create the new assembly that will record visits, based on the prototype.
  /// </summary>
  /// <returns>A representation of the assembly used to record all coverage visits.</returns>
  let internal PrepareAssembly (location:string) =
    let definition = AssemblyDefinition.ReadAssembly(location)
    ProgramDatabase.ReadSymbols definition |> ignore
    definition.Name.Name <- (extractName definition) + ".g"
#if NETCOREAPP2_0
    let pair = None
#else
    use stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("AltCover.Recorder.snk")
    use buffer = new MemoryStream()
    stream.CopyTo(buffer)
    let pair = Some (StrongNameKeyPair(buffer.ToArray()))
#endif
    UpdateStrongNaming definition.Name (pair)

    // set the coverage file path
    let pathGetterDef = definition.MainModule.GetTypes()
                        |> Seq.collect (fun t -> t.Methods)
                        |> Seq.filter (fun m -> m.Name = "get_ReportFile")
                        |> Seq.head

    let body = pathGetterDef.Body
    let worker = body.GetILProcessor();
    let head = body.Instructions.[0]
    worker.InsertBefore(head, worker.Create(OpCodes.Ldstr, Visitor.ReportPath()));
    worker.InsertBefore(head, worker.Create(OpCodes.Ret));

    definition

  /// <summary>
  /// Commit an instrumented assembly to disk
  /// </summary>
  /// <param name="assembly">The instrumented assembly object</param>
  /// <param name="path">The full path of the output file</param>
  /// <remark>Can raise "System.Security.Cryptography.CryptographicException: Keyset does not exist" at random
  /// when asked to strongname.  This writes a new .pdb/.mdb alongside the instrumented assembly</remark>
  let internal WriteAssembly (assembly:AssemblyDefinition) (path:string) =
    let pkey = Mono.Cecil.WriterParameters()
#if NETCOREAPP2_0
    // Assembly with symbols pdb writing fails on .net core on Windows when writing with
    // System.NullReferenceException : Object reference not set to an instance of an object.
    // from deep inside Cecil -- but this works!!
    pkey.WriteSymbols <- true
    pkey.SymbolWriterProvider <- Mono.Cecil.Mdb.MdbWriterProvider() :> ISymbolWriterProvider
#else

    // Assembly with pdb writing fails on mono on Windows when writing with
    // System.NullReferenceException : Object reference not set to an instance of an object.
    // from deep inside Cecil
    // Pdb writing fails on mono on non-Windows with
    // System.DllNotFoundException : ole32.dll
    //  at (wrapper managed-to-native) Mono.Cecil.Pdb.SymWriter:CoCreateInstance
    // Mdb writing now fails in .net framework, it throws
    // Mono.CompilerServices.SymbolWriter.MonoSymbolFileException :
    // Exception of type 'Mono.CompilerServices.SymbolWriter.MonoSymbolFileException' was thrown.
    pkey.WriteSymbols <- true
    pkey.SymbolWriterProvider <- if monoRuntime then Mono.Cecil.Mdb.MdbWriterProvider() :> ISymbolWriterProvider else Mono.Cecil.Pdb.PdbWriterProvider() :> ISymbolWriterProvider

    // Also, there are no strongnames in .net core
    KnownKey assembly.Name
    |> Option.iter (fun key -> pkey.StrongNameKeyPair <- key)
#endif
    assembly.Write(path, pkey)

  type internal SubstituteInstruction (oldValue:Instruction, newValue:Instruction) =
    /// <summary>
    /// Adjust the IL for exception handling
    /// </summary>
    /// <param name="handler">The exception handler</param>
    /// <param name="oldBoundary">The uninstrumented location</param>
    /// <param name="newBoundary">Where it has moved to</param>
    member this.SubstituteExceptionBoundary (handler:ExceptionHandler) =
      if handler.FilterStart = oldValue then handler.FilterStart <- newValue
      if handler.HandlerEnd = oldValue then handler.HandlerEnd <- newValue
      if handler.HandlerStart = oldValue then handler.HandlerStart <- newValue
      if handler.TryEnd = oldValue then handler.TryEnd <- newValue
      if handler.TryStart = oldValue then handler.TryStart <- newValue

    /// <summary>
    /// Adjust the IL to substitute an opcode
    /// </summary>
    /// <param name="instruction">Instruction being processed</param>
    /// <param name="oldOperand">Type we are looking for</param>
    /// <param name="newOperand">Type to replace it with</param>
    member this.SubstituteInstructionOperand(instruction:Instruction) =
      // Performance reasons - only 3 types of operators have operands of Instruction types
      // instruction.Operand getter - is rather slow to execute it for every operator
      match instruction.OpCode.OperandType with
      | OperandType.InlineBrTarget
      | OperandType.ShortInlineBrTarget ->
        if instruction.Operand = (oldValue :> Object) then
           instruction.Operand <- newValue
        // At this point instruction.Operand will be either Operand != oldOperand
        // or instruction.Operand will be of type Instruction[]
        // (in other words - it will be a switch operator's operand)
      | OperandType.InlineSwitch ->
           let operands = instruction.Operand :?> Instruction array
           let offset = operands
                        |> Seq.tryFindIndex (fun x -> x = oldValue)
           match offset with
           | Some i -> // operands.[i] <- newValue : fails with "This expression was expected to have type    ''a [] * int * 'a'    but here has type    'Instruction array'"
                       Array.blit [| newValue |] 0 operands i 1 // so mutate the array like this instead
           | _ -> ()
      | _ -> ()

  let internal InsertVisit (instruction:Instruction) (methodWorker:ILProcessor) (recordingMethodRef:MethodReference) (moduleId:string) (point:int) =
      let counterMethodCall = methodWorker.Create(OpCodes.Call, recordingMethodRef);
      let instrLoadModuleId = methodWorker.Create(OpCodes.Ldstr, moduleId)
      let instrLoadPointId = methodWorker.Create(OpCodes.Ldc_I4, point);

      methodWorker.InsertBefore(instruction, instrLoadModuleId);
      methodWorker.InsertAfter(instrLoadModuleId, instrLoadPointId);
      methodWorker.InsertAfter(instrLoadPointId, counterMethodCall);
      instrLoadModuleId

  /// <summary>
  /// Determine new names for input strongnamed assemblies; if we have a key and
  /// the assembly was already strongnamed then give it the new key token, otherwise
  /// set that there is no strongname.
  /// </summary>
  /// <param name="assembly">The assembly object being operated upon</param>
  /// <param name="path">The names of all assemblies of interest</param>
  /// <returns>Map from input to output names</returns>
  let internal UpdateStrongReferences (assembly : AssemblyDefinition) (assemblies : string list) =
    let effectiveKey = if assembly.Name.HasPublicKey then Visitor.defaultStrongNameKey else None
    UpdateStrongNaming assembly.Name effectiveKey

    // TODO -- is this still lookup table of any use??
    let assemblyReferenceSubstitutions = new Dictionary<String, String>()
    let interestingReferences =  assembly.MainModule.AssemblyReferences
                                 |> Seq.cast<AssemblyNameReference>
                                 |> Seq.filter (fun x -> assemblies |> List.exists (fun y -> y.Equals(x.Name)))

    interestingReferences
    |> Seq.iter (fun r -> let original = r.ToString()
                          let token = KnownToken r
                          let effectiveKey =
#if NETCOREAPP2_0
                                             None
#else
                                             match token with
                                             | None -> Visitor.defaultStrongNameKey
                                                       |> Option.map KeyStore.KeyToRecord
                                             | Some _ -> token
#endif
                          match effectiveKey with
                          | None -> r.HasPublicKey <- false
                                    r.PublicKeyToken <- null
                                    r.PublicKey <- null
                          | Some key -> r.HasPublicKey <- true
                                        r.PublicKey <- key.Pair.PublicKey // implicitly sets token

                          let updated = r.ToString()
                          if  not <| updated.Equals(original, StringComparison.Ordinal) then
                            assemblyReferenceSubstitutions.[original] <- updated
                  )

    assemblyReferenceSubstitutions

  let internal injectJSON json =
    let o = JObject.Parse json

    let target = ((o.Property "runtimeTarget").Value :?> JObject).Property("name").Value.ToString()
    let targets = (o.Properties()
                    |> Seq.find (fun p -> p.Name = "targets")).Value :?> JObject
    let targeted = (targets.Properties()
                    |> Seq.find (fun p -> p.Name= target)).Value :?> JObject

    let app = (targeted.PropertyValues() |> Seq.head)  :?> JObject

    let existingDependencies = app.Properties() |> Seq.tryFind (fun p -> p.Name = "dependencies")
    let prior = match existingDependencies with
                | None -> Set.empty<string>
                | Some p -> (p.Value :?> JObject).Properties()
                            |> Seq.map (fun p -> p.Name)
                            |> Set.ofSeq

    let rawDependencies = (JObject.Parse dependencies).Properties()
                           |> Seq.find (fun p -> p.Name = "dependencies")
    match app.Properties() |> Seq.tryFind (fun p -> p.Name = "dependencies") with
    | None -> app.AddFirst(rawDependencies)
    | Some p -> (rawDependencies.Value :?> JObject).Properties()
                |> Seq.filter (fun r -> prior |> Set.contains r.Name |> not)
                |> Seq.iter (fun r -> (p.Value :?> JObject).Add(r))

    let rt = JObject.Parse runtime
    rt.Properties()
    |> Seq.filter (fun r -> prior |> Set.contains (r.Name.Split('/') |> Seq.head) |> not)
    |> Seq.iter (fun r -> targeted.Add(r))

    let libraries = (o.Properties()
                    |> Seq.find (fun p -> p.Name = "libraries")).Value :?> JObject
    (JObject.Parse newLibraries).Properties()
    |> Seq.filter (fun r -> prior |> Set.contains (r.Name.Split('/') |> Seq.head) |> not)
    |> Seq.rev
    |> Seq.iter (libraries.AddFirst)
    o.ToString()

  /// <summary>
  /// Perform visitor operations
  /// </summary>
  /// <param name="state">Contextual information for the visit</param>
  /// <param name="node">The node being visited</param>
  /// <returns>Updated state</returns>
  let internal InstrumentationVisitor (state : Context) (node:Node) =
     match node with
     | Start _ -> let recorder = typeof<AltCover.Recorder.Tracer>
                  { state with RecordingAssembly = PrepareAssembly(recorder.Assembly.Location) }
     | Assembly (assembly, _, included) -> let updates = UpdateStrongReferences assembly state.InstrumentedAssemblies
                                           if included then
                                              assembly.MainModule.AssemblyReferences.Add(state.RecordingAssembly.Name)
                                           { state with RenameTable = updates } // TODO use this (attribute mappings IIRC)
     | Module (m, _, included) ->
         let restate = match included with
                       | true ->
                         let recordingMethod = match state.RecordingMethod with
                                               | null -> RecordingMethod state.RecordingAssembly
                                               | _ -> state.RecordingMethod

                         { state with
                               RecordingMethodRef = m.ImportReference(recordingMethod);
                               RecordingMethod = recordingMethod }
                       | _ -> state
         { restate with ModuleId = m.Mvid }

     | Type _ ->
         state
     | Method (m, _,  included) ->
         match included with
         | true ->
           let body = m.Body
           { state with
              MethodBody = body;
              MethodWorker = body.GetILProcessor() }
         | _ -> state

     | MethodPoint (instruction, _, point, included) ->
       if included then // by construction the sequence point is included
            let instrLoadModuleId = InsertVisit instruction state.MethodWorker state.RecordingMethodRef (state.ModuleId.ToString()) point

            // Change references in operands from "instruction" to first counter invocation instruction (instrLoadModuleId)
            let subs = SubstituteInstruction (instruction, instrLoadModuleId)
            state.MethodBody.Instructions
            |> Seq.iter subs.SubstituteInstructionOperand

            state.MethodBody.ExceptionHandlers
            |> Seq.iter subs.SubstituteExceptionBoundary

       state
     | AfterMethod included ->
         if included then
            let body = state.MethodBody
            // changes conditional (br.s, brtrue.s ...) operators to corresponding "long" ones (br, brtrue)
            body.SimplifyMacros()
            // changes "long" conditional operators to their short representation where possible
            body.OptimizeMacros()
         state

     | AfterModule -> state
     | AfterAssembly assembly -> let originalFileName = Path.GetFileName assembly.MainModule.FileName
                                 let path = Path.Combine(Visitor.OutputDirectory(), originalFileName)
                                 WriteAssembly assembly path
                                 state
     | Finish -> let counterAssemblyFile = Path.Combine(Visitor.OutputDirectory(), (extractName state.RecordingAssembly) + ".dll")
                 WriteAssembly (state.RecordingAssembly) counterAssemblyFile
                 Directory.GetFiles(Visitor.OutputDirectory(), "*.deps.json", SearchOption.TopDirectoryOnly)
                 |> Seq.iter (fun f -> File.WriteAllText(f, (f |> File.ReadAllText |> injectJSON))                                       )
#if NETCOREAPP2_0
                 let fsharplib = Path.Combine(Visitor.OutputDirectory(), "FSharp.Core.dll")
                 if not (File.Exists fsharplib) then
                   use fsharpbytes = new FileStream(AltCover.Recorder.Tracer.Core(), FileMode.Open, FileAccess.Read)
                   use libstream = new FileStream(fsharplib, FileMode.Create)
                   fsharpbytes.CopyTo libstream
#endif
                 state

  /// <summary>
  /// Higher-order function that returns a visitor
  /// </summary>
  /// <param name="assemblies">List of assembly paths to visit</param>
  /// <returns>Stateful visitor function</returns>
  let internal InstrumentGenerator (assemblies : string list) =
    Visitor.EncloseState InstrumentationVisitor (Context.Build assemblies)