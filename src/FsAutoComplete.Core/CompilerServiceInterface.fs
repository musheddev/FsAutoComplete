namespace FsAutoComplete

open System
open System.IO
open FSharp.Compiler.SourceCodeServices
open Utils
open FSharp.Compiler.Range
open FSharp.Compiler
open FSharp.Compiler.Text

[<RequireQualifiedAccess>]
type FindDeclarationResult =
    | ExternalDeclaration of Decompiler.ExternalContentPosition
    | Range of FSharp.Compiler.Range.range

type ParseAndCheckResults
    (
        parseResults: FSharpParseFileResults,
        checkResults: FSharpCheckFileResults,
        entityCache: EntityCache
    ) =

  member __.TryGetMethodOverrides (lines: LineStr[]) (pos: pos) = async {
    // Find the number of `,` in the current signature
    let commas, _, _ =
      let lineCutoff = pos.Line - 6
      let rec prevPos (line,col) =
        match line, col with
        | 1, 1
        | _ when line < lineCutoff -> 1, 1
        | _, 1 ->
           let prevLine = lines.[line - 2]
           if prevLine.Length = 0 then prevPos(line-1, 1)
           else line - 1, prevLine.Length
        | _    -> line, col - 1

      let rec loop commas depth (line, col) =
        if (line,col) <= (1,1) then (0, line, col) else
        let ch = lines.[line - 1].[col - 1]
        let commas = if depth = 0 && ch = ',' then commas + 1 else commas
        if (ch = '(' || ch = '{' || ch = '[') && depth > 0 then loop commas (depth - 1) (prevPos (line,col))
        elif ch = ')' || ch = '}' || ch = ']' then loop commas (depth + 1) (prevPos (line,col))
        elif ch = '(' || ch = '<' then commas, line, col
        else loop commas depth (prevPos (line,col))
      match loop 0 0 (prevPos(pos.Line, pos.Column)) with
      | _, 1, 1 -> 0, pos.Line, pos.Column
      | newPos -> newPos
    let testPos = mkPos pos.Line (pos.Column - 1)
    // Get the parameter locations
    let paramLocations = parseResults.FindNoteworthyParamInfoLocations pos
    match paramLocations with
    | None -> return ResultOrString.Error "Could not find parameter locations"
    | Some nwpl ->
      let names = nwpl.LongId
      let lidEnd = nwpl.LongIdEndLocation
      let! meth = checkResults.GetMethods(lidEnd.Line, lidEnd.Column, "", Some names)
      return Ok(meth, commas) }

  member __.TryFindDeclaration (pos: pos) (lineStr: LineStr) = async {
    match Lexer.findLongIdents(pos.Column - 1, lineStr) with
    | None -> return ResultOrString.Error "Could not find ident at this location"
    | Some(col, identIsland) ->

      let! declarations = checkResults.GetDeclarationLocation(pos.Line, col, lineStr, identIsland, false)

      let decompile assembly externalSym =
        match Decompiler.tryFindExternalDeclaration checkResults (assembly, externalSym) with
        | Ok extDec -> ResultOrString.Ok (FindDeclarationResult.ExternalDeclaration extDec)
        | Error(Decompiler.FindExternalDeclarationError.ReferenceHasNoFileName assy) -> ResultOrString.Error (sprintf "External declaration assembly '%s' missing file name" assy.SimpleName)
        | Error(Decompiler.FindExternalDeclarationError.ReferenceNotFound assy) -> ResultOrString.Error (sprintf "External declaration assembly '%s' not found" assy)
        | Error(Decompiler.FindExternalDeclarationError.DecompileError (Decompiler.Exception(symbol, file, exn))) ->
          Error (sprintf "Error while decompiling symbol '%A' in file '%s': %s\n%s" symbol file exn.Message exn.StackTrace)

      // attempts to manually discover symbol use and externalsymbol information for a range that doesn't exist in a local file
      // bugfix/workaround for FCS returning invalid declfound for f# members.
      let tryRecoverExternalSymbolForNonexistentDecl (rangeInNonexistentFile: range) = async {
        match Lexer.findLongIdents(pos.Column - 1, lineStr) with
        | None -> return ResultOrString.Error (sprintf "Range for nonexistent file found, no ident found: %s" rangeInNonexistentFile.FileName)
        | Some (col, identIsland) ->
          let! symbolUse = checkResults.GetSymbolUseAtLocation(pos.Line, col, lineStr, identIsland)
          match symbolUse with
          | None -> return ResultOrString.Error (sprintf "Range for nonexistent file found, no symboluse found: %s" rangeInNonexistentFile.FileName)
          | Some sym ->
            let assembly, externalSym =
              match sym with
              | SymbolUse.Field f -> f.Assembly.SimpleName, ExternalSymbol.Field (f.DeclaringEntity.Value.FullName, f.Name)
              | SymbolUse.Function f -> f.Assembly.SimpleName, ExternalSymbol.Field(f.DeclaringEntity.Value.FullName, f.LogicalName)
              | _ ->  failwith "boom"
            return decompile assembly externalSym
      }

      match declarations with
      | FSharpFindDeclResult.DeclNotFound _ ->
        return ResultOrString.Error "Could not find declaration"
      | FSharpFindDeclResult.DeclFound range when range.FileName.EndsWith(Range.rangeStartup.FileName) -> return ResultOrString.Error "Could not find declaration"
      | FSharpFindDeclResult.DeclFound range when System.IO.File.Exists range.FileName ->
        Debug.print "Got a declresult of %A that supposedly exists" range
        return Ok (FindDeclarationResult.Range range)
      | FSharpFindDeclResult.DeclFound rangeInNonexistentFile ->
        Debug.print "Got a declresult of %A that doesn't exist" rangeInNonexistentFile
        return Error ("Could not find declaration")
        // uncomment this to try to workaround the FCS bug
        //return! tryRecoverExternalSymbolForNonexistentDecl rangeInNonexistentFile
      | FSharpFindDeclResult.ExternalDecl (assembly, externalSym) ->
        return decompile assembly externalSym
    }

  member __.TryFindTypeDeclaration (pos: pos) (lineStr: LineStr) = async {
    match Lexer.findLongIdents(pos.Column - 1, lineStr) with
    | None ->
      return Error "Cannot find ident at this location"
    | Some(col,identIsland) ->
      let! symbol = checkResults.GetSymbolUseAtLocation(pos.Line, col, lineStr, identIsland)
      match symbol with
      | None ->
        return Error "Cannot find symbol at this locaion"
      | Some sym ->
        let r =
          match sym with
          | SymbolUse.Field f -> Some f.FieldType.TypeDefinition.DeclarationLocation
          | SymbolUse.Val v -> v.FullTypeSafe |> Option.map (fun f -> f.TypeDefinition.DeclarationLocation)
          | SymbolUse.Entity (e, _) -> Some e.DeclarationLocation
          | SymbolUse.Parameter p -> Some p.Type.TypeDefinition.DeclarationLocation
          | SymbolUse.TypeAbbreviation t -> Some t.DeclarationLocation
          | SymbolUse.Property p -> p.FullTypeSafe |> Option.map (fun f -> f.TypeDefinition.DeclarationLocation)
          | _ -> None
        match r with
        | Some r when File.Exists r.FileName ->
          return (Ok r)
        | _ ->
          return Error "No type information for the symbol at this location"

  }

  member __.TryGetToolTip (pos: pos) (lineStr: LineStr) = async {
    match Lexer.findLongIdents(pos.Column - 1, lineStr) with
    | None -> return ResultOrString.Error "Cannot find ident for tooltip"
    | Some(col,identIsland) ->

      // TODO: Display other tooltip types, for example for strings or comments where appropriate
      let! tip = checkResults.GetToolTipText(pos.Line, col, lineStr, identIsland, FSharpTokenTag.Identifier)
      return
        match tip with
        | FSharpToolTipText(elems) when elems |> List.forall ((=) FSharpToolTipElement.None) ->
            match identIsland with
            | [ident] ->
               KeywordList.tryGetKeywordDescription ident
               |> Option.map (fun desc -> FSharpToolTipText [FSharpToolTipElement.Single(ident, FSharpXmlDoc.Text desc)])
               |> function
               | Some tip ->
                Ok tip
               | None ->
                ResultOrString.Error "No tooltip information"
            | _ ->
              ResultOrString.Error "No tooltip information"
        | _ ->
          Ok(tip)
  }

  member __.TryGetToolTipEnhanced (pos: pos) (lineStr: LineStr) = async {
    match Lexer.findLongIdents(pos.Column - 1, lineStr) with
    | None -> return Error "Cannot find ident for tooltip"
    | Some(col,identIsland) ->

      // TODO: Display other tooltip types, for example for strings or comments where appropriate
      let! tip = checkResults.GetToolTipText(pos.Line, col, lineStr, identIsland, FSharpTokenTag.Identifier)
      let! symbol = checkResults.GetSymbolUseAtLocation(pos.Line, col, lineStr, identIsland)

      match tip with
      | FSharpToolTipText(elems) when elems |> List.forall ((=) FSharpToolTipElement.None) && symbol.IsNone ->
          match identIsland with
          | [ident] ->
             let keyword = KeywordList.tryGetKeywordDescription ident
                           |> Option.map (fun desc -> FSharpToolTipText [FSharpToolTipElement.Single(ident, FSharpXmlDoc.Text desc)])
             match keyword with
             | Some tip ->
              return Ok (tip, ident, "", None)
             | None ->
              return Error "No tooltip information"
          | _ ->
            return Error "No tooltip information"
      | _ ->
      match symbol with
      | None ->
        return Error "No tooltip information"
      | Some symbol ->

        match SignatureFormatter.getTooltipDetailsFromSymbolUse symbol with
        | None ->
          return Error "No tooltip information"
        | Some (signature, footer) ->
            let typeDoc = getTypeIfConstructor symbol.Symbol |> Option.map (fun n -> n.XmlDocSig)
            return Ok (tip, signature, footer, typeDoc)
  }

  member __.TryGetFormattedDocumentation (pos: pos) (lineStr: LineStr) = async {
    match Lexer.findLongIdents(pos.Column - 1, lineStr) with
    | None -> return Error "Cannot find ident"
    | Some(col,identIsland) ->

      // TODO: Display other tooltip types, for example for strings or comments where appropriate
      let! tip = checkResults.GetToolTipText(pos.Line, col, lineStr, identIsland, FSharpTokenTag.Identifier)
      let! symbol = checkResults.GetSymbolUseAtLocation(pos.Line, col, lineStr, identIsland)

      match tip with
      | FSharpToolTipText(elems) when elems |> List.forall ((=) FSharpToolTipElement.None) && symbol.IsNone ->
          match identIsland with
          | [ident] ->
             let keyword = KeywordList.tryGetKeywordDescription ident
                           |> Option.map (fun desc -> FSharpToolTipText [FSharpToolTipElement.Single(ident, FSharpXmlDoc.Text desc)])
             match keyword with
             | Some tip ->
              return Ok (Some tip, None, (ident, (DocumentationFormatter.emptyTypeTip)), "", "")
             | None ->
              return Error "No tooltip information"
          | _ ->
            return Error "No documentation information"
      | _ ->
      match symbol with
      | None ->
        return Error "No documentation information"
      | Some symbol ->
        match DocumentationFormatter.getTooltipDetailsFromSymbolUse symbol with
        | None ->
          return Error "No documentation information"
        | Some (signature, footer, cn) ->
            match symbol with
            | SymbolUse.TypeAbbreviation symbol ->
              return Ok (None, Some (symbol.GetAbbriviatedParent().XmlDocSig, symbol.GetAbbriviatedParent().Assembly.FileName |> Option.getOrElse ""), signature, footer, cn)
            | _ ->
              return Ok (Some tip, None, signature, footer, cn)
  }

  member x.TryGetFormattedDocumentationForSymbol (xmlSig: string) (assembly: string) = async {
    let entities = x.GetAllEntities false
    let ent =
      entities |> List.tryFind (fun e ->
        let check = (e.Symbol.XmlDocSig = xmlSig && e.Symbol.Assembly.SimpleName = assembly)
        if not check then
          match e.Symbol with
          | FSharpEntity (_, abrvEnt, _) ->
            abrvEnt.XmlDocSig = xmlSig && abrvEnt.Assembly.SimpleName = assembly
          | _ -> false
        else
          true
      )
    let ent =
      match ent with
      | Some ent -> Some ent
      | None ->
        entities |> List.tryFind (fun e ->
          let check = (e.Symbol.XmlDocSig = xmlSig)
          if not check then
            match e.Symbol with
            | FSharpEntity (_, abrvEnt, _) ->
              abrvEnt.XmlDocSig = xmlSig
            | _ -> false
          else
            true
        )

    let symbol =
      match ent with
      | Some ent -> Some ent.Symbol
      | None ->
        entities |> List.tryPick (fun e ->
          match e.Symbol with
          | FSharpEntity (ent, _, _) ->
            match ent.MembersFunctionsAndValues |> Seq.tryFind (fun f -> f.XmlDocSig = xmlSig) with
            | Some e -> Some (e :> FSharpSymbol)
            | None ->
              match  ent.FSharpFields |> Seq.tryFind (fun f -> f.XmlDocSig = xmlSig) with
              | Some e -> Some (e :> FSharpSymbol)
              | None -> None
          | _ ->
            None
        )

    match symbol with
    | None -> return Error "No matching symbol information"
    | Some symbol ->
      match DocumentationFormatter.getTooltipDetailsFromSymbol symbol with
      | None ->
        return Error "No tooltip information"
      | Some (signature, footer, cn) ->
          return Ok (symbol.XmlDocSig, symbol.Assembly.FileName |> Option.getOrElse "", symbol.XmlDoc |> Seq.toList , signature, footer, cn)
  }

  member __.TryGetSymbolUse (pos: pos) (lineStr: LineStr) =
    async {
        match Lexer.findLongIdents(pos.Column - 1, lineStr) with
        | None ->
          return (ResultOrString.Error "No ident at this location")
        | Some(colu, identIsland) ->

        let! symboluse = checkResults.GetSymbolUseAtLocation(pos.Line, colu, lineStr, identIsland)
        match symboluse with
        | None ->
          return (ResultOrString.Error "No symbol information found")
        | Some symboluse ->

        let! symboluses = checkResults.GetUsesOfSymbolInFile symboluse.Symbol
        return Ok (symboluse, symboluses) }

  member __.TryGetSignatureData (pos: pos) (lineStr: LineStr) =
    async {
        match Lexer.findLongIdents(pos.Column - 1, lineStr) with
        | None ->
          return (ResultOrString.Error "No ident at this location")
        | Some(colu, identIsland) ->

        let! symboluse = checkResults.GetSymbolUseAtLocation(pos.Line, colu, lineStr, identIsland)
        match symboluse with
        | None ->
          return (ResultOrString.Error "No symbol information found")
        | Some symboluse ->
          let fsym = symboluse.Symbol
          match fsym with
          | :? FSharpMemberOrFunctionOrValue as symbol ->
            let typ = symbol.ReturnParameter.Type.Format symboluse.DisplayContext
            if symbol.IsPropertyGetterMethod then
                return Ok(typ, [], [])
            else
              let parms =
                symbol.CurriedParameterGroups
                |> Seq.map (Seq.map (fun p -> p.DisplayName, p.Type.Format symboluse.DisplayContext) >> Seq.toList )
                |> Seq.toList
              let generics =
                symbol.GenericParameters
                |> Seq.map (fun generic ->
                    generic.Name
                )
                |> Seq.toList
              // Abstract members and abstract member overrides with one () parameter seem have a list with an empty list
              // as parameters.
              match parms with
              | [ [] ] when symbol.IsMember && (not symbol.IsPropertyGetterMethod) ->
                return Ok(typ, [ [ ("unit", "unit") ] ], [])
              | _ ->
                return Ok(typ, parms, generics)
          | _ ->
            return (ResultOrString.Error "Not a member, function or value" )
    }

  member __.TryGetF1Help (pos: pos) (lineStr: LineStr) =
    async {
        match Lexer.findLongIdents(pos.Column - 1, lineStr) with
        | None -> return (ResultOrString.Error "No ident at this location")
        | Some(colu, identIsland) ->

        let! help = checkResults.GetF1Keyword(pos.Line, colu, lineStr, identIsland)
        match help with
        | None -> return (ResultOrString.Error "No symbol information found")
        | Some hlp -> return Ok hlp}

  member __.TryGetCompletions (pos: pos) (lineStr: LineStr) filter (getAllSymbols : unit -> AssemblySymbol list) = async {
    try
      let longName = FSharp.Compiler.QuickParse.GetPartialLongNameEx(lineStr, pos.Column - 2)
      let residue = longName.PartialIdent

      let getAllSymbols() =
        getAllSymbols()
        |> List.filter (fun entity -> entity.FullName.Contains "." && not (PrettyNaming.IsOperatorName entity.Symbol.DisplayName))

      let token = Lexer.getSymbol pos.Line (pos.Column - 1) lineStr SymbolLookupKind.Simple [||]
      match token with
      | Some k when k.Kind = Other -> return None
      | Some k when k.Kind = Operator  -> return None
      | Some k when k.Kind = Keyword  -> return None
      | _ ->

      let! results = checkResults.GetDeclarationListInfo(Some parseResults, pos.Line, lineStr, longName, getAllSymbols)

      let getKindPriority = function
        | CompletionItemKind.CustomOperation -> -1
        | CompletionItemKind.Property -> 0
        | CompletionItemKind.Field -> 1
        | CompletionItemKind.Method (isExtension = false) -> 2
        | CompletionItemKind.Event -> 3
        | CompletionItemKind.Argument -> 4
        | CompletionItemKind.Other -> 5
        | CompletionItemKind.Method (isExtension = true) -> 6

      let sortedDeclItems =
          results.Items
          |> Array.sortWith (fun x y ->
              let mutable n = (not x.IsResolved).CompareTo(not y.IsResolved)
              if n <> 0 then n else
                  n <- (getKindPriority x.Kind).CompareTo(getKindPriority y.Kind)
                  if n <> 0 then n else
                      n <- (not x.IsOwnMember).CompareTo(not y.IsOwnMember)
                      if n <> 0 then n else
                          n <- StringComparer.OrdinalIgnoreCase.Compare(x.Name, y.Name)
                          if n <> 0 then n else
                            x.MinorPriority.CompareTo(y.MinorPriority))
      let decls =
        match filter with
        | Some "StartsWith" -> [| for d in sortedDeclItems do if d.Name.StartsWith(residue, StringComparison.InvariantCultureIgnoreCase) then yield d |]
        | Some "Contains" -> [| for d in sortedDeclItems do if d.Name.IndexOf(residue, StringComparison.InvariantCultureIgnoreCase) >= 0 then yield d |]
        | _ -> sortedDeclItems



      let shouldKeywords = decls.Length > 0 && not results.IsForType && not results.IsError && List.isEmpty longName.QualifyingIdents
      return Some (decls, residue, shouldKeywords)
    with :? TimeoutException -> return None
  }

  member __.GetAllEntities (publicOnly: bool) : AssemblySymbol list =
      try
        let res = [
          yield! AssemblyContentProvider.getAssemblySignatureContent AssemblyContentType.Full checkResults.PartialAssemblySignature
          let ctx = checkResults.ProjectContext
          let assembliesByFileName =
            ctx.GetReferencedAssemblies()
            |> Seq.groupBy (fun asm -> asm.FileName)
            |> Seq.map (fun (fileName, asms) -> fileName, List.ofSeq asms)
            |> Seq.toList
            |> List.rev // if mscorlib.dll is the first then FSC raises exception when we try to
                        // get Content.Entities from it.

          for fileName, signatures in assembliesByFileName do
            let contentType = if publicOnly then Public else Full
            let content = AssemblyContentProvider.getAssemblyContent entityCache.Locking contentType fileName signatures
            yield! content
        ]
        res
      with
      | _ -> []

  member __.GetAllSymbolUsesInFile () = checkResults.GetAllUsesOfAllSymbolsInFile()

  member __.GetSemanticClassification = checkResults.GetSemanticClassification None
  member __.GetAST = parseResults.ParseTree
  member __.GetCheckResults = checkResults
  member __.GetParseResults = parseResults
  member __.FileName = parseResults.FileName

type Version = int

type FSharpCompilerServiceChecker(backgroundServiceEnabled) =
  let checker =
    FSharpChecker.Create(
      projectCacheSize = (if backgroundServiceEnabled then 3 else 200),
      keepAllBackgroundResolutions = not backgroundServiceEnabled,
      keepAssemblyContents = false,
      suggestNamesForErrors = true)

  do checker.ImplicitlyStartBackgroundWork <- not backgroundServiceEnabled
  do checker.BeforeBackgroundFileCheck.Add ignore

  /// FCS only accepts absolute file paths, so this ensures that by
  /// rooting relative paths onto HOME on *nix and %HOMRDRIVE%%HOMEPATH% on windows
  let ensureAbsolutePath path =
    if (try Path.GetFullPath path |> ignore; true with _ -> false) then path
    else
        match Environment.OSVersion.Platform with
        | PlatformID.Unix
        | PlatformID.MacOSX -> Environment.GetEnvironmentVariable "HOME"
        | _ -> Environment.ExpandEnvironmentVariables "%HOMEDRIVE%%HOMEPATH%"
        </> Path.GetFileName path

  let entityCache = EntityCache()

  /// the root path to the dotnet sdk installations, eg /usr/local/share/dotnet
  let mutable sdkRoot = None
  /// the chosen version of the dotnet sdk for deriving F# compiler FSI references, eg 3.0.100
  let mutable sdkVersion = lazy(None)
  /// the chosen version of the dotnet runtime for deriving BCL references, eg 3.0.0
  let mutable runtimeVersion = lazy(None)
  /// the map of assemblyNames and file paths derived from the sdkVersion and runtimeVersion
  let mutable discoveredAssembliesByName = lazy(Map.empty)
  /// additional arguments that are added to typechecking of scripts
  let mutable fsiAdditionalArguments = Array.empty

  /// This event is raised when any data that impacts script typechecking
  /// is changed. This can potentially invalidate existing project options
  /// so we must purge any typecheck results for cripts.
  let scriptTypecheckRequirementsChanged = Event<_>()

  let mutable disableInMemoryProjectReferences = false

  /// evaluates the set of assemblies found given the current sdkRoot/sdkVersion/runtimeVersion
  let computeAssemblyMap () =
    match sdkRoot, sdkVersion.Value, runtimeVersion.Value with
    | None, _, _ ->
      Debug.print "No dotnet SDK root path found"
      Map.empty
    | Some root, None, None ->
      Debug.print "Couldn't find latest 3.x sdk and runtime versions inside root %s" root
      Map.empty
    | Some root, None, _ ->
      Debug.print "Couldn't find latest 3.x sdk version inside root %s" root
      Map.empty
    | Some root, _, None ->
      Debug.print "Couldn't find latest 3.x runtime version inside root %s" root
      Map.empty
    | Some dotnetSdkRoot, Some sdkVersion, Some runtimeVersion ->
      let refs = FSIRefs.netCoreRefs dotnetSdkRoot (string sdkVersion) (string runtimeVersion) Environment.fsiTFMMoniker true
      Debug.print "found refs for SDK %O/Runtime %O/TFM %s:\n%A" sdkVersion runtimeVersion Environment.fsiTFMMoniker refs
      refs
      |> List.map (fun path -> Path.GetFileNameWithoutExtension path, path)
      |> Map.ofList

  let clearProjectReferences (opts: FSharpProjectOptions) =
    if disableInMemoryProjectReferences then {opts with ReferencedProjects = [||]} else opts

  let logDebug fmt =
    if Debug.verbose then Debug.print "[FSharpChecker] Current Queue Length: %d" checker.CurrentQueueLength
    Debug.print fmt

  /// replace any BCL/FSharp.Core/FSI refs that FCS gives us with our own set, which is more probe-able
  let replaceFrameworkRefs (projOptions: FSharpProjectOptions) =
    let fcsAndScriptReferences =
      projOptions.OtherOptions
      |> Array.choose (fun r ->
        if r.StartsWith("-r")
        then
          let path = r.[3..]
          let assemblyName = Path.GetFileNameWithoutExtension path
          Some(assemblyName, path)
        else None
      )
      |> Map.ofArray

    let mergedRefs =
      // we combine here taking our framework references first and throwing away theirs.
      // this is important because #r order influences typechecking
      Map.combineTakeFirst discoveredAssembliesByName.Value fcsAndScriptReferences
      |> Map.values
      |> Seq.map (fun r -> "-r:" + r)
      |> Array.ofSeq

    { projOptions with OtherOptions = mergedRefs }

  let applyFSIAdditionalArgs (projOptions: FSharpProjectOptions) =
    { projOptions with OtherOptions = Array.append fsiAdditionalArguments projOptions.OtherOptions }

  member __.CreateFCSBinder(netFwInfo: Dotnet.ProjInfo.Workspace.NetFWInfo, loader: Dotnet.ProjInfo.Workspace.Loader) =
    Dotnet.ProjInfo.Workspace.FCS.FCSBinder(netFwInfo, loader, checker)

  member __.DisableInMemoryProjectReferences
    with get() = disableInMemoryProjectReferences
    and set(value) = disableInMemoryProjectReferences <- value

  member __.GetDependingProjects (file: FilePath) (options : seq<string * FSharpProjectOptions>) =
    let project = options |> Seq.tryFind (fun (k,_) -> k.ToUpperInvariant() = file.ToUpperInvariant())
    project |> Option.map (fun (_, option) ->
      option, [
        yield! options
               |> Seq.map snd
               |> Seq.distinctBy (fun o -> o.ProjectFileName)
               |> Seq.filter (fun o -> o.ReferencedProjects |> Array.map (fun (_,v) -> Path.GetFullPath v.ProjectFileName) |> Array.contains option.ProjectFileName )
      ])

  member private __.GetNetFxScriptOptions(file, source) = async {
    logDebug "[Opts] Getting NetFX options for script file %s" file
    let! (opts, errors) = checker.GetProjectOptionsFromScript(file, SourceText.ofString source, assumeDotNetFramework = true, useFsiAuxLib = true)
    let allModifications = applyFSIAdditionalArgs
    return allModifications opts, errors
  }

  member private __.GetNetCoreScriptOptions(file, source) = async {
    logDebug "[Opts] Getting NetCore options for script file %s" file
    let! (opts, errors) = checker.GetProjectOptionsFromScript(file, SourceText.ofString source, assumeDotNetFramework = false, useSdkRefs = true, useFsiAuxLib = true)
    let allModifications = replaceFrameworkRefs >> applyFSIAdditionalArgs
    return allModifications opts, errors
  }

  member self.GetProjectOptionsFromScript(file, source, tfm) = async {
    let! (projOptions, errors) =
      match tfm with
      | FSIRefs.TFM.NetFx ->
        self.GetNetFxScriptOptions(file, source)
      | FSIRefs.TFM.NetCore ->
        self.GetNetCoreScriptOptions(file, source)

    match errors with
    | [] ->
      let refs = projOptions.OtherOptions |> Array.filter (fun o -> o.StartsWith("-r")) |> String.concat "\n"
      logDebug "[Opts] Resolved options - %A" projOptions
      logDebug "[Opts] Resolved references:\n%s" refs
    | errs ->
      logDebug "[Opts] Resolved options with errors\n%A\n%A" projOptions errs

    match FakeSupport.detectFakeScript file with
    | None ->
      logDebug "[Opts] %s is not a FAKE script" file
      return projOptions
    | Some (detectionInfo) ->
      logDebug "[Opts] %s is a FAKE script" file
      try
        let otherOpts = FakeSupport.getProjectOptions detectionInfo
        logDebug "[Opts] Discovered FAKE options - %A" otherOpts
        return { projOptions with OtherOptions = otherOpts }
      with e ->
        logDebug "[Opts] Error in FAKE script support: %O" e
        return projOptions
  }

  member __.GetBackgroundCheckResultsForFileInProject(fn, opt) =
    logDebug "[Checker] GetBackgroundCheckResultsForFileInProject - %s" fn
    let opt = clearProjectReferences opt
    checker.GetBackgroundCheckResultsForFileInProject(fn, opt)
    |> Async.map (fun (pr,cr) ->  ParseAndCheckResults (pr, cr, entityCache))

  member __.FileChecked =
    checker.FileChecked

  member __.ScriptTypecheckRequirementsChanged =
    scriptTypecheckRequirementsChanged.Publish

  member __.ParseFile(fn, source, fpo) =
    logDebug "[Checker] ParseFile - %s" fn
    let source = SourceText.ofString source
    checker.ParseFile(fn, source, fpo)

  member __.ParseAndCheckFileInProject(filePath, version, source, options) =
    async {
      let opName = sprintf "ParseAndCheckFileInProject - %s" filePath
      logDebug "[Checker] %s" opName
      let source = SourceText.ofString source
      let options = clearProjectReferences options
      let fixedFilePath = ensureAbsolutePath filePath
      let! res = Async.Catch (checker.ParseAndCheckFileInProject (fixedFilePath, version, source, options, userOpName = opName))
      return
          match res with
          | Choice1Of2 (p,c)->
            let parseErrors = p.Errors |> Array.map (fun p -> p.Message)
            match c with
            | FSharpCheckFileAnswer.Aborted ->
              logDebug "[Checker] %s completed with errors %A" opName (List.ofArray p.Errors)
              ResultOrString.Error (sprintf "Check aborted (%A). Errors: %A" c parseErrors)
            | FSharpCheckFileAnswer.Succeeded(c) ->
              Ok (ParseAndCheckResults(p,c, entityCache))
          | Choice2Of2 e -> ResultOrString.Error e.Message
    }

  member __.TryGetRecentCheckResultsForFile(file, options, ?source) =
    logDebug "[Checker] TryGetRecentCheckResultsForFile - %s" file
    let source = source |> Option.map SourceText.ofString
    let options = clearProjectReferences options
    checker.TryGetRecentCheckResultsForFile(file, options, ?sourceText=source)
    |> Option.map (fun (pr, cr, _) -> ParseAndCheckResults (pr, cr, entityCache))

  member x.GetUsesOfSymbol (file, options : (SourceFilePath * FSharpProjectOptions) seq, symbol : FSharpSymbol) = async {
    logDebug "[Checker] GetUsesOfSymbol - %s" file
    let projects = x.GetDependingProjects file options
    return!
      match projects with
      | None -> async {return [||]}
      | Some (p, projects) -> async {
        let! res =
          [yield p; yield! projects ]
          |> Seq.map (fun (opts) -> async {
              let opts = clearProjectReferences opts
              let! res = checker.ParseAndCheckProject opts
              return! res.GetUsesOfSymbol symbol
            })
          |> Async.Parallel
        return res |> Array.collect id }
  }

  member __.GetDeclarations (fileName, source, options, version) = async {
    logDebug "[Checker] GetDeclarations - %s" fileName
    let source = SourceText.ofString source
    let! parseResult = checker.ParseFile(fileName, source, options)
    return parseResult.GetNavigationItems().Declarations
  }

  member __.Compile = checker.Compile

  member internal __.GetFSharpChecker() = checker

  member __.SetDotnetRoot(path) =
    if sdkRoot = Some path
    then ()
    else
      sdkRoot <- Some path
      sdkVersion <- Environment.latest3xSdkVersion path
      runtimeVersion <- Environment.latest3xRuntimeVersion path
      discoveredAssembliesByName <- lazy(computeAssemblyMap ())
      scriptTypecheckRequirementsChanged.Trigger ()

  member __.GetDotnetRoot () = sdkRoot

  member __.SetFSIAdditionalArguments args =
    if fsiAdditionalArguments = args
    then ()
    else
      fsiAdditionalArguments <- args
      scriptTypecheckRequirementsChanged.Trigger ()