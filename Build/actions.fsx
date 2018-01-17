open System
open System.IO
open System.Reflection
open System.Xml
open System.Xml.Linq

open Fake
open FSharp.Markdown
open YamlDotNet.RepresentationModel

module Actions =
  let Clean () =
    let rec Clean1 depth =
      try
        (DirectoryInfo ".").GetDirectories("*", SearchOption.AllDirectories)
        |> Seq.filter (fun x -> x.Name.StartsWith "_" || x.Name = "bin" || x.Name = "obj")
        |> Seq.map (fun x -> x.FullName)
        |> Seq.distinct
        // arrange so leaves get deleted first, avoiding "does not exist" warnings
        |> Seq.groupBy (fun x -> x |> Seq.filter (fun c -> c='\\' || c = '/') |> Seq.length)
        |> Seq.map (fun (n,x) -> (n, x |> Seq.sort))
        |> Seq.sortBy (fun (n,x) -> -1 * n)
        |> Seq.map (fun (n,x) -> x)
        |> Seq.concat
        |> Seq.iter (fun n -> printfn "Deleting %s" n
                              Directory.Delete(n, true))

        let temp = Environment.GetEnvironmentVariable("TEMP")
        if not <| String.IsNullOrWhiteSpace temp then
            Directory.GetFiles(temp, "*.tmp.dll.mdb")
            |> Seq.iter File.Delete
       with
       | :? System.IO.IOException as x -> Clean' (x :> Exception) depth
       | :? System.UnauthorizedAccessException as x -> Clean' (x :> Exception) depth
    and Clean' x depth =
      printfn "looping after %A" x
      System.Threading.Thread.Sleep(500)
      if depth < 10 then Clean1 (depth + 1)

    Clean1 0

  let template ="""namespace AltCover
open System.Reflection
open System.Runtime.CompilerServices
#if DEBUG
[<assembly: AssemblyConfiguration("Debug {0}")>]
#else
[<assembly: AssemblyConfiguration("Release {0}")>]
#endif
#if NETSTANDARD2_0
[<assembly: InternalsVisibleTo("AltCover.Shadow.Tests")>]
#else
#if NETCOREAPP2_0
[<assembly: InternalsVisibleTo("AltCover.Tests")>]

#else
[<assembly: InternalsVisibleTo("AltCover.Tests, PublicKey={1}")>]
[<assembly: InternalsVisibleTo("AltCover.Tests, PublicKey={2}")>]
[<assembly: InternalsVisibleTo("AltCover.Shadow.Tests, PublicKey={1}")>]
[<assembly: InternalsVisibleTo("AltCover.Shadow.Tests, PublicKey={2}")>]
[<assembly: InternalsVisibleTo("AltCover.Shadow.Tests2, PublicKey={1}")>]
[<assembly: InternalsVisibleTo("AltCover.Shadow.Tests2, PublicKey={2}")>]
#endif
#endif
()
"""

  let InternalsVisibleTo version =
    let stream2 = new System.IO.FileStream("./Build/SelfTest.snk", System.IO.FileMode.Open, System.IO.FileAccess.Read)
    let pair2 = StrongNameKeyPair(stream2)
    let key2 = BitConverter.ToString pair2.PublicKey

    let stream = new System.IO.FileStream("./Build/Infrastructure.snk", System.IO.FileMode.Open, System.IO.FileAccess.Read)
    let pair = StrongNameKeyPair(stream)
    let key = BitConverter.ToString pair.PublicKey

    let file = String.Format(System.Globalization.CultureInfo.InvariantCulture,
                template, version, key.Replace("-", String.Empty), key2.Replace("-", String.Empty))

    let path = "_Generated/VisibleToTest.fs"
    // Update the file only if it would change
    let old = if File.Exists(path) then File.ReadAllText(path) else String.Empty
    if not (old.Equals(file)) then File.WriteAllText(path, file)

  let GetVersionFromYaml () =
    use yaml = new FileStream("appveyor.yml", FileMode.Open, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.SequentialScan)
    use yreader = new StreamReader(yaml)
    let ystream = new YamlStream()
    ystream.Load(yreader)
    let mapping = ystream.Documents.[0].RootNode :?> YamlMappingNode
    string mapping.Children.[YamlScalarNode("version")]

  let LocalVersion appveyor (version:string) =
    let now = DateTimeOffset.UtcNow
    let epoch = DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan(int64 0))
    let diff = now.Subtract(epoch)
    let fraction = diff.Subtract(TimeSpan.FromDays(float diff.Days))
    let revision= ((int fraction.TotalSeconds) / 3)

    let majmin = String.Join(".", version.Split('.') |> Seq.take 2)
    let result = if String.IsNullOrWhiteSpace appveyor then sprintf "%s.%d.%d" majmin diff.Days revision else appveyor
    printfn "Build version : %s" version
    (result, majmin, now.Year)

  let FixMVId files =
    // Fix up symbol file to have the MVId emitted by the System.Reflection.Emit code
    files
    |> Seq.iter (fun f -> let assembly = System.Reflection.Assembly.ReflectionOnlyLoadFrom (Path.GetFullPath f)
                          let mvid = assembly.ManifestModule.ModuleVersionId.ToByteArray();
                          let symbols = System.IO.File.ReadAllBytes(f + ".mdb")
                          mvid |> Array.iteri (fun i x -> symbols.[i+16] <- x)
                          System.IO.File.WriteAllBytes(f + ".mdb", symbols))

  let ValidateFSharpTypes simpleReport others =
    use coverageFile = new FileStream(simpleReport, FileMode.Open, FileAccess.Read, FileShare.None, 4096, FileOptions.SequentialScan)
    // Edit xml report to store new hits
    let coverageDocument = XDocument.Load(XmlReader.Create(coverageFile))
    let recorded = coverageDocument.Descendants(XName.Get("method"))
                   |> Seq.map (fun x -> x.Attribute(XName.Get("name")).Value)
                   |> Seq.filter (fun x -> others |> Seq.exists (fun y -> x = y) |> not)
                   |> Seq.sort
                   |> Seq.toList
    let expected = "Invoke as_bar bytes get_MyBar makeThing returnBar returnFoo testMakeThing testMakeUnion"
    if recorded.Length <> 9 then failwith (sprintf "Bad method list length %A" recorded)
    if String.Join(" ", recorded) <> expected then failwith (sprintf "Bad method list %A" recorded)

  let ValidateSample1 simpleReport sigil =
    // get recorded details from here
    use coverageFile = new FileStream(simpleReport, FileMode.Open, FileAccess.Read, FileShare.None, 4096, FileOptions.SequentialScan)
    let coverageDocument = XDocument.Load(XmlReader.Create(coverageFile))
    let recorded = coverageDocument.Descendants(XName.Get("seqpnt"))
                   |> Seq.toList

    let zero = recorded
               |> Seq.filter (fun x -> x.Attribute(XName.Get("visitcount")).Value = "0")
               |> Seq.map (fun x -> x.Attribute(XName.Get("line")).Value)
               |> Seq.sort
               |> Seq.toList
    let ones = recorded
               |> Seq.filter (fun x -> x.Attribute(XName.Get("visitcount")).Value = "1")
               |> Seq.map (fun x -> x.Attribute(XName.Get("line")).Value)
               |> Seq.sort
               |> Seq.toList

    if (List.length ones) + (List.length zero) <> (List.length recorded) then failwith ("unexpected visits in " + sigil)
    let zero' = zero |> Seq.distinct |> Seq.toList

    if ["18"; "19"; "20"] <> zero' then failwith ("wrong unvisited in " + sigil + " : " + (sprintf "%A" zero'))

    let ones' = ones |> Seq.distinct |> Seq.toList
    if ["11"; "12"; "13"; "14"; "15"; "16"; "21"] <> ones' then failwith ("wrong number of visited  in " + sigil + " : " + (sprintf "%A" ones'))

  let SimpleInstrumentingRun (samplePath:string) (binaryPath:string) (reportSigil:string) =
    printfn "Instrument and run a simple executable"
    ensureDirectory "./_Reports"
    let simpleReport = (FullName "./_Reports") @@ ( reportSigil + ".xml")
    let binRoot = FullName binaryPath
    let sampleRoot = FullName samplePath
    let instrumented = "__Instrumented." + reportSigil
    let result = ExecProcess (fun info -> info.FileName <- binRoot @@ "AltCover.exe"
                                          info.WorkingDirectory <- sampleRoot
                                          info.Arguments <- ("\"-t=System\\.\" -x=" + simpleReport + " /o=./" + instrumented)) (TimeSpan.FromMinutes 5.0)
    if result <> 0 then failwith "Simple instrumentation failed"
    let result2 = ExecProcess (fun info -> info.FileName <- sampleRoot @@ (instrumented + "/Sample1.exe")
                                           info.WorkingDirectory <- (sampleRoot @@ instrumented)
                                           info.Arguments <- "") (TimeSpan.FromMinutes 5.0)
    if result2 <> 0 then failwith "Instrumented .exe failed"
    ValidateSample1 simpleReport reportSigil

  let SimpleInstrumentingRunUnderMono (samplePath:string) (binaryPath:string) (reportSigil':string) (monoOnWindows:string option)=
   printfn "Instrument and run a simple executable under mono"
   match monoOnWindows with
   | Some mono ->
    ensureDirectory "./_Reports"
    let reportSigil = reportSigil' + "UnderMono"
    let simpleReport = (FullName "./_Reports") @@ ( reportSigil + ".xml")
    let binRoot = FullName binaryPath
    let sampleRoot = FullName samplePath
    let instrumented = "__Instrumented." + reportSigil
    let result = ExecProcess (fun info -> info.FileName <- mono
                                          info.WorkingDirectory <- sampleRoot
                                          info.Arguments <- ((binRoot @@ "AltCover.exe") + " \"-t=System\\.\" -x=" + simpleReport + " /o=./" + instrumented)) (TimeSpan.FromMinutes 5.0)
    if result <> 0 then failwith "Simple instrumentation failed"
    let result2 = ExecProcess (fun info -> info.FileName <- sampleRoot @@ (instrumented + "/Sample1.exe")
                                           info.WorkingDirectory <- (sampleRoot @@ instrumented)
                                           info.Arguments <- "") (TimeSpan.FromMinutes 5.0)
    if result2 <> 0 then failwith "Instrumented .exe failed"
    ValidateSample1 simpleReport reportSigil
   | None -> failwith "Mono executable expected"

let PrepareReadMe packingCopyright =
    let readme = FullName "README.md"
    let document = File.ReadAllText readme
    let docHtml = """<?xml version="1.0"  encoding="utf-8"?>
<!DOCTYPE html>
<html lang="en">
<head>
<title>AltCover README</title>
</head>
<body>
"""               + (Markdown.TransformHtml document) + """
<footer><p style="text-align: center">""" + packingCopyright + """</p>
</footer>
</body>
</html>
"""
    let xmlform = XDocument.Parse docHtml
    let body = xmlform.Descendants(XName.Get "body")
    let eliminate = [ "Continuous Integration"; "Building"; "Thanks to" ]
    let keep = ref true

    let kill = body.Elements()
               |> Seq.map (fun x -> match x.Name.LocalName with
                                    | "h2" -> keep := (List.tryFind (fun e -> e = String.Concat(x.Nodes())) eliminate) |> Option.isNone
                                    | "footer" -> keep := true
                                    | _ -> ()
                                    if !keep then None else Some x)
               |> Seq.toList
    kill |>
    Seq.iter (fun q -> match q with
                       | Some x -> x.Remove()
                       | _ -> ())

    let packable = FullName "./_Binaries/README.html"
    xmlform.Save packable