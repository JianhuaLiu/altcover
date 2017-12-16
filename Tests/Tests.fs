﻿namespace Tests

open System
open System.IO
open System.Reflection
open System.Xml.Linq

open AltCover
open AltCover.Augment
open AltCover.Filter
open Mono.Cecil
open N
open NUnit.Framework

[<TestFixture>]
type AltCoverTests() = class
  // Hack for running while instrumented
  static member private Hack () =
    let where = Assembly.GetExecutingAssembly().Location;
    let def = Mono.Cecil.AssemblyDefinition.ReadAssembly(where)
    let pdb = AltCover.ProgramDatabase.GetPdbFromImage(def)
    match pdb with
    | None -> "\\.."
    | _ -> String.Empty

  // Augment.fs

  [<Test>]
  member self.AugmentNullableDetectNulls() =
    let input = [ "string"; null; "another string" ]
    let nulls = input |> Seq.map (Option.nullable >> Option.isNone)
    Assert.That(nulls, Is.EquivalentTo([false; true; false]))

  [<Test>]
  member self.AugmentGetOrElseFillsInNone() =
    let input = [ "string"; null; "another string" ]
    let strings = input |> Seq.map (Option.nullable >> (Option.getOrElse "fallback"))
    Assert.That(strings, Is.EquivalentTo([ "string"; "fallback"; "another string" ]))

  // ProgramDatabase.fs

  [<Test>]
  member self.ShouldGetPdbFromImage() =
    // Hack for running while instrumented
    let where = Assembly.GetExecutingAssembly().Location
    let files = Directory.GetFiles(Path.GetDirectoryName(where) + AltCoverTests.Hack())
    files
    |> Seq.filter (fun x -> x.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                            || x.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
    |> Seq.map (fun x -> (x, Mono.Cecil.AssemblyDefinition.ReadAssembly x))
    |> Seq.filter (fun x -> (snd x).FullName.EndsWith("PublicKeyToken=c02b1a9f5b7cade8", StringComparison.OrdinalIgnoreCase))
    |> Seq.iter( fun x ->let pdb = AltCover.ProgramDatabase.GetPdbFromImage (snd x)
                         match pdb with
                         | None -> Assert.Fail("No .pdb for " + (fst x))
                         | Some name ->
                            let probe = Path.ChangeExtension((fst x), ".pdb")
                            let file = FileInfo(probe)
                            let filename = file.Name
                            Assert.That(name, Does.EndWith("\\" + filename), (fst x) + " -> " + name) )

  [<Test>]
  member self.ShouldGetPdbWithFallback() =
    // Hack for running while instrumented
    let where = Assembly.GetExecutingAssembly().Location
    let files = Directory.GetFiles(Path.GetDirectoryName(where) + AltCoverTests.Hack())
    files
    |> Seq.filter (fun x -> x.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                            || x.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
    |> Seq.iter( fun x ->
      let def = Mono.Cecil.AssemblyDefinition.ReadAssembly x
      let pdb = AltCover.ProgramDatabase.GetPdbWithFallback(def)
      match pdb with
      | None -> Assert.That(File.Exists(Path.ChangeExtension(x, ".pdb")), Is.Not.True, "No .pdb for " + x)
      | Some name ->
         let probe = Path.ChangeExtension(x, ".pdb")
         let file = FileInfo(probe)
         let filename = file.Name
         Assert.That(name, Does.EndWith("\\" + filename), x + " -> " + name)
    )

  [<Test>]
  member self.ShouldGetMdbWithFallback() =
    // Hack for running while instrumented
    let where = Assembly.GetExecutingAssembly().Location
    let files = Directory.GetFiles(where.Substring(0, where.IndexOf("_Binaries")) + "_Mono\\Sample1")
    files
    |> Seq.filter (fun x -> x.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                            || x.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
    |> Seq.iter( fun x ->
      let def = Mono.Cecil.AssemblyDefinition.ReadAssembly x
      let mdb = AltCover.ProgramDatabase.GetPdbWithFallback(def)
      match mdb with
      | None -> Assert.That(File.Exists(x + ".mdb"), Is.Not.True, "No .mdb for " + x)
      | Some name ->
         let probe = x + ".mdb"
         let file = FileInfo(probe)
         let filename = file.Name
         Assert.That(name + ".mdb", Does.EndWith("\\" + filename), x + " -> " + name)
    )

  [<Test>]
  member self.ShouldGetSymbolsFromPdb() =
    // Hack for running while instrumented
    let where = Assembly.GetExecutingAssembly().Location
    let files = Directory.GetFiles(Path.GetDirectoryName(where) + AltCoverTests.Hack())
    files
    |> Seq.filter (fun x -> x.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                            || x.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
    |> Seq.map Mono.Cecil.AssemblyDefinition.ReadAssembly
    |> Seq.filter (fun x -> x.FullName.EndsWith("PublicKeyToken=c02b1a9f5b7cade8", StringComparison.OrdinalIgnoreCase))
    |> Seq.iter (fun def ->
      AltCover.ProgramDatabase.ReadSymbols def
      Assert.That (def.MainModule.HasSymbols, def.MainModule.FullyQualifiedName)
    )

  [<Test>]
  member self.ShouldNotGetSymbolsWhenNoPdb() =
    // Hack for running while instrumented
    let where = Assembly.GetExecutingAssembly().Location
    let files = Directory.GetFiles(Path.GetDirectoryName(where) + AltCoverTests.Hack())
    files
    |> Seq.filter (fun x -> x.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                            || x.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
    |> Seq.filter (fun x -> Path.GetFileName(x).StartsWith("FSharp", StringComparison.OrdinalIgnoreCase))
    |> Seq.map Mono.Cecil.AssemblyDefinition.ReadAssembly
    |> Seq.filter (fun x -> not <| x.FullName.EndsWith("PublicKeyToken=c02b1a9f5b7cade8", StringComparison.OrdinalIgnoreCase))
    |> Seq.iter (fun def ->
      AltCover.ProgramDatabase.ReadSymbols def
      printfn "%A" def.MainModule.FullyQualifiedName
      Assert.That (not def.MainModule.HasSymbols, def.MainModule.FullyQualifiedName)
    )

  [<Test>]
  member self.ShouldGetSymbolsFromMdb() =
    // Hack for running while instrumented
    let where = Assembly.GetExecutingAssembly().Location
    let files = Directory.GetFiles(where.Substring(0, where.IndexOf("_Binaries")) + "_Mono\\Sample1")
    files
    |> Seq.filter (fun x -> x.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                            || x.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
    |> Seq.iter( fun x ->
      let def = Mono.Cecil.AssemblyDefinition.ReadAssembly x
      AltCover.ProgramDatabase.ReadSymbols def
      Assert.That (def.MainModule.HasSymbols, def.MainModule.FullyQualifiedName)
    )

  // Filter.fs

  [<Test>]
  member self.FiltersCanBeCompared() = // This works around OpenCover issue #615
     Assert.That ((FilterClass.Type "23").Equals(FilterClass.Attribute "42"), Is.False)

  [<Test>]
  member self.NoneOfTheAboveMatchesNoType() =
     Assert.That (Match () (FilterClass.Type "23"), Is.False)

  [<Test>]
  member self.NoneOfTheAboveMatchesNoAttribute() =
     Assert.That (Match () (FilterClass.Attribute "23"), Is.False)

  [<Test>]
  member self.NoneOfTheAboveMatchesNoAssembly() =
     Assert.That (Match () (FilterClass.Assembly "23"), Is.False)

  [<Test>]
  member self.NoneOfTheAboveMatchesNoFile() =
     Assert.That (Match () (FilterClass.File "23"), Is.False)

  [<Test>]
  member self.NoneOfTheAboveMatchesNoMethod() =
     Assert.That (Match () (FilterClass.Method "23"), Is.False)

  [<Test>]
  member self.FileDoesNotMatchNonFileClass() =
     Assert.That (Match (Assembly.GetExecutingAssembly().Location) (FilterClass.Type "23"), Is.False)

  [<Test>]
  member self.FileDoesMatchFileClass() =
     Assert.That (Match (Assembly.GetExecutingAssembly().Location) (FilterClass.File "Cove"), Is.True)

  [<Test>]
  member self.AssemblyDoesNotMatchNonAssemblyClass() =
     let def = Mono.Cecil.AssemblyDefinition.ReadAssembly (Assembly.GetExecutingAssembly().Location)
     Assert.That (Match def (FilterClass.Type "23"), Is.False)

  [<Test>]
  member self.AssemblyDoesMatchAssemblyClass() =
     let def = Mono.Cecil.AssemblyDefinition.ReadAssembly (Assembly.GetExecutingAssembly().Location)
     Assert.That (Match def (FilterClass.Assembly "Cove"), Is.True)

  [<Test>]
  member self.TypeDoesNotMatchNonTypeClass() =
     let def = Mono.Cecil.AssemblyDefinition.ReadAssembly (Assembly.GetExecutingAssembly().Location)
     def.MainModule.Types
     |> Seq.iter (fun t -> Assert.That (Match t (FilterClass.File "23"), Is.False))

  [<Test>]
  member self.TypeDoesMatchTypeClass() =
     let def = Mono.Cecil.AssemblyDefinition.ReadAssembly (Assembly.GetExecutingAssembly().Location)
     def.MainModule.Types
     |> Seq.filter (fun t -> t.IsPublic)  // exclude the many compiler generted chaff classes
     |> Seq.iter (fun t -> Assert.That (Match t (FilterClass.Type "Cove"), Is.True))

  [<Test>]
  member self.MethodDoesNotMatchNonMethodClass() =
     let def = Mono.Cecil.AssemblyDefinition.ReadAssembly (Assembly.GetExecutingAssembly().Location)
     def.MainModule.Types
     |> Seq.filter (fun t -> t.IsPublic) 
     |> Seq.collect (fun t -> t.Methods)
     |> Seq.iter (fun m -> Assert.That (Match m (FilterClass.Type "23"), Is.False))

  [<Test>]
  member self.MethodDoesMatchMethodClass() =
     let def = Mono.Cecil.AssemblyDefinition.ReadAssembly (Assembly.GetExecutingAssembly().Location)
     Assert.That(def.MainModule.Types
                 |> Seq.filter (fun t -> t.IsPublic)  // exclude the many compiler generted chaff classes
                 |> Seq.collect (fun t -> t.Methods)
                 |> Seq.filter (fun m -> m.IsPublic && (not m.IsConstructor))
                 |> Seq.filter (fun m -> Match m (FilterClass.Method "Augment"))
                 |> Seq.length,
                 Is.EqualTo(2))

  [<Test>]
  member self.AttributeDoesNotMatchNonAttributeClass() =
     let def = Mono.Cecil.AssemblyDefinition.ReadAssembly (Assembly.GetExecutingAssembly().Location)
     def.MainModule.Types
     |> Seq.iter (fun t -> Assert.That (Match t.CustomAttributes (FilterClass.File "23"), Is.False))

  [<Test>]
  member self.AttributeDoesMatchAttributeClass() =
     let def = Mono.Cecil.AssemblyDefinition.ReadAssembly (Assembly.GetExecutingAssembly().Location)
     def.MainModule.Types
     |> Seq.filter (fun t -> t.IsPublic)  // exclude the many compiler generted chaff classes
     |> Seq.iter (fun t -> Assert.That (Match t (FilterClass.Attribute "Fix"), Is.True))

  [<Test>]
  member self.Sample3Class1IsCSharpAutoproperty() =
     let sample3 = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "Sample3.dll")
     let def = Mono.Cecil.AssemblyDefinition.ReadAssembly (sample3)
     def.MainModule.Types
     |> Seq.filter(fun t -> t.Name = "Class1")
     |> Seq.collect (fun t ->t.Methods)
     |> Seq.filter (fun m -> m.IsGetter || m.IsSetter)
     |> Seq.iter (IsCSharpAutoProperty >> Assert.That)

  [<Test>]
  member self.Sample3Class2IsNotCSharpAutoproperty() =
     let sample3 = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "Sample3.dll")
     let def = Mono.Cecil.AssemblyDefinition.ReadAssembly (sample3)
     def.MainModule.Types
     |> Seq.filter(fun t -> t.Name = "Class2")
     |> Seq.collect (fun t -> t.Methods)
     |> Seq.filter (fun m -> m.IsGetter || m.IsSetter)
     |> Seq.iter (fun m -> Assert.That(IsCSharpAutoProperty m, Is.False))
 
  [<Test>]
  member self.CanIdentifyExcludedFSharpMethods() =
    let tracer = DU.returnFoo 23
    let location = tracer.GetType().Assembly.Location
    let sourceAssembly = AssemblyDefinition.ReadAssembly(location)

    let direct = sourceAssembly.MainModule.Types
                 |> Seq.filter (fun x -> x.Namespace = "N" )
                 |> Seq.toList
    let indirect = direct
                   |> Seq.filter (fun t-> t.HasNestedTypes)
                   |> Seq.collect (fun t -> t.NestedTypes)
                   |> Seq.toList // MyUnion, MyThing
    let indirect2 = indirect
                   |> Seq.filter (fun t-> t.HasNestedTypes)
                   |> Seq.collect (fun t -> t.NestedTypes)
                   |> Seq.toList // Foo, Bar, ...
    
    let indirect3 = indirect2
                    |> Seq.filter (fun t-> t.HasNestedTypes)
                    |> Seq.collect (fun t -> t.NestedTypes)
                    |> Seq.map (fun t -> t.FullName)
                    |> Seq.toList

    Assert.That (indirect3
                   |> Seq.isEmpty, sprintf "Third order types found %A" indirect3)

    let pass = Seq.concat [direct; indirect; indirect2]
               |> Seq.collect (fun t -> t.Methods)
               |> Seq.filter (not << Filter.IsFSharpInternal)
               |> Seq.map (fun x -> x.Name)
               |> Seq.sort
               |> Seq.toList

    let expected = [  ".ctor" ; "Invoke"; "as_bar"; "bytes"; "get_MyBar" ; "makeThing"; "returnBar"; "returnFoo"; "testMakeThing"; "testMakeUnion" ]

    Assert.That(pass, Is.EquivalentTo(expected), sprintf "Got sequence %A" pass);

  [<Test>]
  member self.CanIdentifyExcludedCSharpAutoProperties() =
    let location = typeof<Sample3.Class1>.Assembly.Location
    let sourceAssembly = AssemblyDefinition.ReadAssembly(location)

    let direct = sourceAssembly.MainModule.Types
                 |> Seq.filter (fun x -> x.Name = "Class1" )
                 |> Seq.head
    let pass = direct.Methods
               |> Seq.filter (not << Filter.IsCSharpAutoProperty)
               |> Seq.map (fun x -> x.Name)
               |> Seq.sort
               |> Seq.toList

    let expected = [".ctor"]
    Assert.That(pass, Is.EquivalentTo(expected), sprintf "Got sequence %A" pass);

  [<Test>]
  member self.CanIdentifyIncludedCSharpProperties() =
    let location = typeof<Sample3.Class1>.Assembly.Location
    let sourceAssembly = AssemblyDefinition.ReadAssembly(location)

    let direct = sourceAssembly.MainModule.Types
                 |> Seq.filter (fun x -> x.Name = "Class2" )
                 |> Seq.head
    let pass = direct.Methods
               |> Seq.filter (not << Filter.IsCSharpAutoProperty)
               |> Seq.map (fun x -> x.Name)
               |> Seq.sort
               |> Seq.toList

    let expected = [".ctor"; "get_Property"; "set_Property"]
    Assert.That(pass, Is.EquivalentTo(expected), sprintf "Got sequence %A" pass);

  // Visitor.fs

  [<Test>]
  member self.NodesCanBeCompared() = // This works around OpenCover issue #615
     Assert.That ((Node.AfterModule).Equals(Node.Finish), Is.False)

  [<Test>]
  member self.KeyRecordsCanBeCompared() = // This works around OpenCover issue #615
     let record1 = { Pair = null; Token = [] }
     let record2 = { Pair = null; Token = [ 23uy ] }
     Assert.That (record1.Equals(record2), Is.False)

  [<Test>]
  member self.EmptyArrayHasExpectedHash() = 
    Assert.That ((KeyStore.TokenOfArray [| |]), Is.EquivalentTo [|9uy; 7uy; 216uy; 175uy; 144uy; 24uy; 96uy; 149uy|])

  static member private ProvideKeyPair () =
      use stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Infrastructure.snk")
      use buffer = new MemoryStream()
      stream.CopyTo(buffer)
      StrongNameKeyPair(buffer.ToArray())

  [<Test>]
  member self.KeyHasExpectedToken() = 
    let token = KeyStore.TokenOfKey <| AltCoverTests.ProvideKeyPair ()
    let token' = String.Join(String.Empty, token |> List.map (fun x -> x.ToString("x2")))
    Assert.That (token', Is.EqualTo("c02b1a9f5b7cade8"))

  [<Test>]
  member self.TokenGeneratesExpectedULong() =
    let token = [|1uy; 0uy; 0uy; 0uy; 0uy; 0uy; 0uy; 0uy|]
    Assert.That (KeyStore.TokenAsULong token, Is.EqualTo(1UL))

  [<Test>]
  member self.KeyHasExpectedIndex() = 
    let token = KeyStore.KeyToIndex <| AltCoverTests.ProvideKeyPair ()
    Assert.That (token, Is.EqualTo(0xe8ad7c5b9f1a2bc0UL))

  [<Test>]
  member self.EmptyArrayHasExpectedIndex() = 
    Assert.That ((KeyStore.ArrayToIndex [| |]), Is.EqualTo(0x95601890afd80709UL))

  [<Test>]
  member self.KeyHasExpectedRecord() = 
    let pair = AltCoverTests.ProvideKeyPair ()
    let token = KeyStore.KeyToRecord <| pair
    Assert.That (token, Is.EqualTo({Pair = pair; Token = BitConverter.GetBytes(0xe8ad7c5b9f1a2bc0UL) |> Array.toList}))
 
  [<Test>]
  member self.VisitorDetectNulls() =
    let input = [ "string"; null; "another string" ]
    let nulls = input |> Seq.map (Visitor.isNotNull >> not)
    Assert.That(nulls, Is.EquivalentTo([false; true; false]))

  [<Test>]
  member self.KeyHasExpectedPlaceInIndex() = 
    try
      Assert.That (Visitor.keys.Keys.Count, Is.EqualTo(0))
      let pair = AltCoverTests.ProvideKeyPair ()
      Visitor.Add(pair)
      Assert.That(Visitor.keys.ContainsKey(0xe8ad7c5b9f1a2bc0UL))
      Assert.That(Visitor.keys.[0xe8ad7c5b9f1a2bc0UL], Is.EqualTo({Pair = pair; Token = BitConverter.GetBytes(0xe8ad7c5b9f1a2bc0UL) |> Array.toList}))
    finally
      Visitor.keys.Clear()

  [<Test>]
  member self.EmptyFiltersPassAll() = 
    Assert.That (Visitor.NameFilters.Count, Is.EqualTo(0))
    Assert.That (Visitor.IsIncluded self)

  [<Test>]
  member self.NonEmptyFiltersCatchAnExpectedValue() = 
    try
      Assert.That (Visitor.NameFilters.Count, Is.EqualTo(0))
      Visitor.NameFilters.AddRange([ FilterClass.File "Cove"; FilterClass.Method "Augment"])
      Assert.That (Visitor.IsIncluded (Assembly.GetExecutingAssembly().Location), Is.False)
    finally
      Visitor.NameFilters.Clear()

  [<Test>]
  member self.NonEmptyFiltersPassAnExpectedValue() = 
    try
      Assert.That (Visitor.NameFilters.Count, Is.EqualTo(0))
      Visitor.NameFilters.AddRange([ FilterClass.File "System"; FilterClass.Method "Augment"])
      Assert.That (Visitor.IsIncluded (Assembly.GetExecutingAssembly().Location))
    finally
      Visitor.NameFilters.Clear()

  [<Test>]
  member self.AfterProcessingYieldsAnExpectedValue() = 
    let def = Mono.Cecil.AssemblyDefinition.ReadAssembly (Assembly.GetExecutingAssembly().Location)
    let inputs = [ Node.Start [] ; Node.Assembly (def, true) ; Node.Module (null, false) ; Node.Type (null, true) ;
                   Node.Method (null, false) ; Node.MethodPoint ( null, null, 0, true ) ;
                   Node.AfterMethod false ; Node.AfterModule ; Node.AfterAssembly def; Node.Finish ]
    let outputs = inputs |> Seq.map (Visitor.After >> Seq.toList)
    let expected = [ [Finish]; [AfterAssembly def]; [AfterModule]; []; [AfterMethod false]; []; []; []; []; []]
    Assert.That (outputs, Is.EquivalentTo (expected))

  [<Test>]
  member self.Sample3Class1PropertyIsNotSignificant() =
     let sample3 = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "Sample3.dll")
     let def = Mono.Cecil.AssemblyDefinition.ReadAssembly (sample3)
     def.MainModule.Types
     |> Seq.filter(fun t -> t.Name = "Class1")
     |> Seq.collect (fun t -> t.Methods)
     |> Seq.filter (fun m -> m.IsGetter || m.IsSetter)
     |> Seq.iter (fun m -> Assert.That(Visitor.significant m, Is.False))

  [<Test>]
  member self.Sample3Class2IPropertyIsSignificant() =
     let sample3 = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "Sample3.dll")
     let def = Mono.Cecil.AssemblyDefinition.ReadAssembly (sample3)
     def.MainModule.Types
     |> Seq.filter(fun t -> t.Name = "Class2")
     |> Seq.collect (fun t -> t.Methods)
     |> Seq.filter (fun m -> m.IsGetter || m.IsSetter)
     |> Seq.iter (Visitor.significant >> Assert.That)


(*
  static member TTBaseline = "<?xml version=\"1.0\" encoding=\"utf-8\"?>
<?xml-stylesheet href=\"coverage.xsl\" type=\"text/xsl\"?>
<coverage profilerVersion=\"0\" driverVersion=\"0\" startTime=\"\" measureTime=\"\">
<module moduleId=\"\" name=\"Sample1.exe\" assembly=\"Sample1\" assemblyIdentity=\"Sample1, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null\">
<method name=\"Main\" class=\"TouchTest.Program\" metadataToken=\"0\" excluded=\"false\" instrumented=\"true\" >
<seqpnt visitcount=\"1\" line=\"11\" column=\"9\"  endline=\"11\" endcolumn=\"10\" excluded=\"false\" document=\"Sample1\\Program.cs\" />
<seqpnt visitcount=\"1\" line=\"12\" column=\"13\" endline=\"12\" endcolumn=\"36\" excluded=\"false\" document=\"Sample1\\Program.cs\" />
<seqpnt visitcount=\"1\" line=\"13\" column=\"13\" endline=\"13\" endcolumn=\"33\" excluded=\"false\" document=\"Sample1\\Program.cs\" />
<seqpnt visitcount=\"1\" line=\"14\" column=\"13\" endline=\"14\" endcolumn=\"14\" excluded=\"false\" document=\"Sample1\\Program.cs\" />
<seqpnt visitcount=\"1\" line=\"15\" column=\"17\" endline=\"15\" endcolumn=\"63\" excluded=\"false\" document=\"Sample1\\Program.cs\" />
<seqpnt visitcount=\"1\" line=\"16\" column=\"13\" endline=\"16\" endcolumn=\"14\" excluded=\"false\" document=\"Sample1\\Program.cs\" />
<seqpnt visitcount=\"0\" line=\"18\" column=\"13\" endline=\"18\" endcolumn=\"14\" excluded=\"false\" document=\"Sample1\\Program.cs\" />
<seqpnt visitcount=\"0\" line=\"19\" column=\"17\" endline=\"19\" endcolumn=\"62\" excluded=\"false\" document=\"Sample1\\Program.cs\" />
<seqpnt visitcount=\"0\" line=\"20\" column=\"13\" endline=\"20\" endcolumn=\"14\" excluded=\"false\" document=\"Sample1\\Program.cs\" />
<seqpnt visitcount=\"1\" line=\"21\" column=\"9\"  endline=\"21\" endcolumn=\"10\" excluded=\"false\" document=\"Sample1\\Program.cs\" />
</method>
</module>
</coverage>"

  static member private RecursiveValidate result expected depth zero =
    let rcount = result |> Seq.length
    let ecount = expected |> Seq.length

    Assert.That(rcount, Is.EqualTo(ecount), "Mismatch at depth " + depth.ToString())

    Seq.zip result expected |> Seq.iter (fun ((r:XElement), (e:XElement)) ->
            Assert.That(r.Name, Is.EqualTo(e.Name), "Expected name " + e.Name.ToString())
            let ra = r.Attributes()
            let ea = e.Attributes()
            Seq.zip ra ea |> Seq.iter (fun ((a1:XAttribute), (a2:XAttribute)) ->
                    Assert.That(a1.Name, Is.EqualTo(a2.Name))
                    match a1.Name.ToString() with
                    | "moduleId"
                    | "metadataToken"
                    | "startTime"
                    | "measureTime" -> ()
                    | "document" -> Assert.That(a1.Value, Does.EndWith(a2.Value),
                                      a1.Name.ToString() + " : " + r.ToString() + " -> document")
                    | "visitcount" -> let expected = if zero then "0" else a2.Value
                                      Assert.That(a1.Value, Is.EqualTo(expected), r.ToString() + " -> visitcount")
                    | _ -> Assert.That(a1.Value, Is.EqualTo(a2.Value), r.ToString() + " -> " + a1.Name.ToString())
                )

            AltCoverTests.RecursiveValidate (r.Elements()) (e.Elements()) (depth+1) zero)

  [<Test;Ignore("Temporarily disable")>]
  member self.ShouldGenerateExpectedXmlReport() =
    let visitor, document = Report.ReportGenerator()
    // Hack for running while instrumented
    let where = Assembly.GetExecutingAssembly().Location;
    let path = Path.Combine(Path.GetDirectoryName(where) + AltCoverTests.Hack(), "Sample1.exe")

    Visitor.Visit [ visitor ] (Visitor.ToSeq path)

    let baseline = XDocument.Load(new System.IO.StringReader(AltCoverTests.TTBaseline))
    let result = document.Elements()
    let expected = baseline.Elements()
    AltCoverTests.RecursiveValidate result expected 0 true
*)
end