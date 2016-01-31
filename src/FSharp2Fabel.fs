[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Fabel.FSharp2Fabel

open Microsoft.FSharp.Compiler
open Microsoft.FSharp.Compiler.Ast
open Microsoft.FSharp.Compiler.SourceCodeServices
open Fabel.AST
open Fabel.FSharp2Fabel.Util

let rec private transformExpr com ctx fsExpr =
    match fsExpr with
    (** ## Erased *)
    | BasicPatterns.Coerce(_targetType, Transform com ctx inpExpr) -> inpExpr
    | BasicPatterns.NewDelegate(_delegateType, Transform com ctx delegateBodyExpr) -> delegateBodyExpr
    // TypeLambda is a local generic lambda
    // e.g, member x.Test() = let typeLambda x = x in typeLambda 1, typeLambda "A"
    | BasicPatterns.TypeLambda (_genArgs, Transform com ctx lambda) -> lambda

    | BasicPatterns.ILAsm (_asmCode, _typeArgs, argExprs) ->
        // printfn "ILAsm detected in %A: %A" fsExpr.Range fsExpr // TODO: Check
        match argExprs with
        | [] -> Fabel.Value Fabel.Null
        | [Transform com ctx expr] -> expr
        | exprs -> Fabel.Sequential (List.map (transformExpr com ctx) exprs, makeRangeFrom fsExpr)

    (** ## Flow control *)
    | BasicPatterns.FastIntegerForLoop(Transform com ctx start, Transform com ctx limit, body, isUp) ->
        match body with
        | BasicPatterns.Lambda (BindIdent com ctx (newContext, ident), body) ->
            Fabel.For (ident, start, limit, com.Transform newContext body, isUp)
            |> makeLoop fsExpr
        | _ -> failwithf "Unexpected loop in %A: %A" fsExpr.Range fsExpr

    | BasicPatterns.WhileLoop(Transform com ctx guardExpr, Transform com ctx bodyExpr) ->
        Fabel.While (guardExpr, bodyExpr)
        |> makeLoop fsExpr

    // This must appear before BasicPatterns.Let
    | ForOf (BindIdent com ctx (newContext, ident), Transform com ctx value, body) ->
        Fabel.ForOf (ident, value, transformExpr com newContext body)
        |> makeLoop fsExpr

    (** Values *)
    | BasicPatterns.Const(value, _typ) ->
        makeConst value

    | BasicPatterns.BaseValue typ ->
        makeType com typ |> Fabel.Super |> Fabel.Value 

    | BasicPatterns.ThisValue typ ->
        makeType com typ |> Fabel.This |> Fabel.Value 

    | BasicPatterns.Value thisVar when thisVar.IsMemberThisValue ->
        makeType com thisVar.FullType |> Fabel.This |> Fabel.Value 

    | BasicPatterns.Value v ->
        if not v.IsModuleValueOrMember then
            let (GetIdent com ctx ident) = v
            Fabel.Value (Fabel.IdentValue ident)
        else
            match v.FullName with
            | "Microsoft.FSharp.Core.Operators.seq" ->
                Fabel.ImportRef (Naming.getCoreLibPath com, Some "Seq")
                |> Fabel.Value
            | "Microsoft.FSharp.Core.ExtraTopLevelOperators.async" ->
                Fabel.ImportRef (Naming.getCoreLibPath com, Some "Async")
                |> Fabel.Value
            | _ ->
                let typeRef =
                    makeTypeFromDef com v.EnclosingEntity
                    |> Fabel.TypeRef |> Fabel.Value
                Fabel.Get (typeRef, makeConst v.DisplayName, makeType com fsExpr.Type)

    | BasicPatterns.DefaultValue (FabelType com typ) ->
        let valueKind =
            match typ with
            | Fabel.PrimitiveType Fabel.Boolean -> Fabel.BoolConst false
            | Fabel.PrimitiveType (Fabel.Number kind) -> Fabel.NumberConst (U2.Case1 0, kind)
            | _ -> Fabel.Null
        Fabel.Value valueKind

    (** ## Assignments *)
    // TODO: Possible optimization if binding to another ident (let x = y), just replace it in the ctx
    | BasicPatterns.Let((BindIdent com ctx (newContext, ident) as var,
                            Transform com ctx value), body) ->
        let body = transformExpr com newContext body
        let assignment = Fabel.VarDeclaration (ident, value, var.IsMutable) 
        match body with
        // Check if this this is just a wrapper to a call as it happens in pipelines
        // e.g., let x = 5 in fun y -> methodCall x y
        | Fabel.Value(Fabel.Lambda(args2, (Fabel.Apply(callee, ReplaceArgs [ident, value] args, cons,_,_) as body))) ->
            Fabel.Lambda (args2, Fabel.Apply (callee, args, cons, body.Type, body.Range))
            |> Fabel.Value
        | _ -> makeSequential (makeRangeFrom fsExpr) [assignment; body]

    | BasicPatterns.LetRec(recBindings, body) ->
        let ctx, idents =
            (recBindings, (ctx, [])) ||> List.foldBack (fun (var,_) (ctx, idents) ->
                let (BindIdent com ctx (newContext, ident)) = var
                (newContext, ident::idents))
        let assignments =
            recBindings
            |> List.map2 (fun ident (var, Transform com ctx binding) ->
                Fabel.VarDeclaration (ident, binding, var.IsMutable)) idents
        assignments @ [transformExpr com ctx body] 
        |> makeSequential (makeRangeFrom fsExpr)

    (** ## Applications *)
    | BasicPatterns.TraitCall (_sourceTypes, traitName, _typeArgs, _typeInstantiation, argExprs) ->
        // printfn "TraitCall detected in %A: %A" fsExpr.Range fsExpr // TODO: Check
        let callee, args = transformExpr com ctx argExprs.Head, List.map (transformExpr com ctx) argExprs.Tail
        let callee = Fabel.Get (callee, makeConst traitName, makeFnType args)
        Fabel.Apply (callee, args, false, makeType com fsExpr.Type, makeRangeFrom fsExpr)

    // TODO: Check `inline` annotation?
    // TODO: Watch for restParam attribute
    | BasicPatterns.Call(callee, meth, _typeArgs1, _typeArgs2, args) ->
        makeCall com ctx fsExpr callee meth args

    | BasicPatterns.Application(Transform com ctx expr, _typeArgs, args) ->
        makeApply com ctx fsExpr expr (List.map (transformExpr com ctx) args)
        
    | BasicPatterns.IfThenElse (Transform com ctx guardExpr, Transform com ctx thenExpr, Transform com ctx elseExpr) ->
        Fabel.IfThenElse (guardExpr, thenExpr, elseExpr, makeRangeFrom fsExpr)

    | BasicPatterns.TryFinally (BasicPatterns.TryWith(body, _, _, catchVar, catchBody),finalBody) ->
        makeTryCatch com ctx fsExpr body (Some (catchVar, catchBody)) (Some finalBody)

    | BasicPatterns.TryFinally (body, finalBody) ->
        makeTryCatch com ctx fsExpr body None (Some finalBody)

    | BasicPatterns.TryWith (body, _, _, catchVar, catchBody) ->
        makeTryCatch com ctx fsExpr body (Some (catchVar, catchBody)) None

    | BasicPatterns.Sequential (Transform com ctx first, Transform com ctx second) ->
        makeSequential (makeRangeFrom fsExpr) [first; second]

    (** ## Lambdas *)
    | BasicPatterns.Lambda (var, body) ->
        makeLambda com ctx [var] body

    (** ## Getters and Setters *)
    | BasicPatterns.ILFieldGet (callee, typ, fieldName) ->
        failwithf "Found unsupported ILField reference in %A: %A" fsExpr.Range fsExpr

    // TODO: Check if it's FSharpException
    // TODO: Change name of automatically generated fields
    | BasicPatterns.FSharpFieldGet (callee, FabelType com calleeType, FieldName fieldName) ->
        let callee =
            match callee with
            | Some (Transform com ctx callee) -> callee
            | None -> makeTypeRef calleeType
        Fabel.Get (callee, makeConst fieldName, makeType com fsExpr.Type)

    | BasicPatterns.TupleGet (_tupleType, tupleElemIndex, Transform com ctx tupleExpr) ->
        Fabel.Get (tupleExpr, makeConst tupleElemIndex, makeType com fsExpr.Type)

    // Single field: Item; Multiple fields: Item1, Item2...
    | BasicPatterns.UnionCaseGet (Transform com ctx unionExpr, FabelType com unionType, unionCase, FieldName fieldName) ->
        match unionType with
        | ErasedUnion | OptionUnion -> unionExpr
        | ListUnion -> failwith "TODO: List"
        | OtherType -> Fabel.Get (unionExpr, makeConst fieldName, makeType com fsExpr.Type)

    | BasicPatterns.ILFieldSet (callee, typ, fieldName, value) ->
        failwithf "Found unsupported ILField reference in %A: %A" fsExpr.Range fsExpr

    // TODO: Change name of automatically generated fields
    | BasicPatterns.FSharpFieldSet (callee, FabelType com calleeType, FieldName fieldName, Transform com ctx value) ->
        let callee =
            match callee with
            | Some (Transform com ctx callee) -> callee
            | None -> makeTypeRef calleeType
        Fabel.Set (callee, Some (makeConst fieldName), value, makeRangeFrom fsExpr)

    | BasicPatterns.UnionCaseTag (Transform com ctx unionExpr, _unionType) ->
        Fabel.Get (unionExpr, makeConst "Tag", makeType com fsExpr.Type)

    // We don't need to check if this an erased union, as union case values are only set
    // in constructors, which are ignored for erased unions
    | BasicPatterns.UnionCaseSet (Transform com ctx unionExpr, _type, _case, FieldName caseField, Transform com ctx valueExpr) ->
        Fabel.Set (unionExpr, Some (makeConst caseField), valueExpr, makeRangeFrom fsExpr)

    | BasicPatterns.ValueSet (GetIdent com ctx valToSet, Transform com ctx valueExpr) ->
        Fabel.Set (Fabel.IdentValue valToSet |> Fabel.Value, None, valueExpr, makeRangeFrom fsExpr)

    (** Instantiation *)
    | BasicPatterns.NewArray(FabelType com typ, argExprs) ->
        let arrayKind =
            match typ with
            | Fabel.PrimitiveType (Fabel.Number numberKind) ->
                Fabel.TypedArray numberKind
            | _ -> Fabel.DynamicArray
        (argExprs |> List.map (transformExpr com ctx), arrayKind)
        |> Fabel.ArrayConst |> Fabel.Value

    | BasicPatterns.NewTuple(_, argExprs) ->
        (argExprs |> List.map (transformExpr com ctx), Fabel.Tuple)
        |> Fabel.ArrayConst |> Fabel.Value

    | BasicPatterns.ObjectExpr(_objType, _baseCallExpr, _overrides, interfaceImplementations) ->
        failwith "TODO"

    // TODO: Check for erased constructors with property assignment (Call + Sequential)
    | BasicPatterns.NewObject(meth, _typeArgs, args) ->
        makeCall com ctx fsExpr None meth args

    // TODO: Check if it's FSharpException
    // TODO: Create constructors for Record and Union types
    | BasicPatterns.NewRecord(FabelType com recordType, argExprs) ->
        let argExprs = argExprs |> List.map (transformExpr com ctx)
        Fabel.Apply (makeTypeRef recordType, argExprs, true,
            makeType com fsExpr.Type, makeRangeFrom fsExpr)

    | BasicPatterns.NewUnionCase(FabelType com unionType, unionCase, argExprs) ->
        let argExprs = argExprs |> List.map (transformExpr com ctx)
        match unionType with
        | ErasedUnion | OptionUnion ->
            match argExprs with
            | [] -> Fabel.Value Fabel.Null 
            | [expr] -> expr
            | _ -> failwithf "Erased Union Cases must have one single field: %A" unionType
        | ListUnion ->
            match unionCase.Name with
            | "Cons" -> Fabel.Apply (makeCoreRef com "List",
                            (makeConst "Cons")::argExprs, true,
                            makeType com fsExpr.Type, makeRangeFrom fsExpr)
            | _ -> Fabel.Value Fabel.Null
        | OtherType ->
            // Include Tag name in args
            let argExprs = (makeConst unionCase.Name)::argExprs
            Fabel.Apply (makeTypeRef unionType, argExprs, true,
                    makeType com fsExpr.Type, makeRangeFrom fsExpr)

    (** ## Type test *)
    | BasicPatterns.TypeTest (FabelType com typ as fsTyp, Transform com ctx expr) ->
        makeTypeTest com (makeRangeFrom fsExpr) typ expr 

    | BasicPatterns.UnionCaseTest (Transform com ctx unionExpr, FabelType com unionType, unionCase) ->
        let boolType = Fabel.PrimitiveType Fabel.Boolean
        match unionType with
        | ErasedUnion ->
            if unionCase.UnionCaseFields.Count <> 1 then
                failwithf "Erased Union Cases must have one single field: %A" unionType
            else
                let typ = makeType com unionCase.UnionCaseFields.[0].FieldType
                makeTypeTest com (makeRangeFrom fsExpr) typ unionExpr
        | OptionUnion | ListUnion ->
            let opKind =
                if (unionCase.Name = "None" || unionCase.Name = "Empty")
                then BinaryEqual
                else BinaryUnequal
            makeBinOp (makeRangeFrom fsExpr) boolType [unionExpr; Fabel.Value Fabel.Null] opKind 
        | OtherType ->
            let left = Fabel.Get (unionExpr, makeConst "Tag", Fabel.PrimitiveType Fabel.String)
            let right = makeConst unionCase.Name
            makeBinOp (makeRangeFrom fsExpr) boolType [left; right] BinaryEqualStrict

    (** Pattern Matching *)
    | BasicPatterns.DecisionTreeSuccess (decIndex, decBindings) ->
        match Map.tryFind decIndex ctx.decisionTargets with
        | None -> failwith "Missing decision target"
        // If we get a reference to a function, call it
        | Some (TargetRef targetRef) ->
            Fabel.Apply (Fabel.IdentValue targetRef |> Fabel.Value,
                (decBindings |> List.map (transformExpr com ctx)),
                false, makeType com fsExpr.Type, makeRangeFrom fsExpr)
        // If we get an implementation without bindings, just transform it
        | Some (TargetImpl ([], Transform com ctx decBody)) -> decBody
        // If we have bindings, create the assignments
        | Some (TargetImpl (decVars, decBody)) ->
            let newContext, assignments =
                List.foldBack2 (fun var (Transform com ctx binding) (accContext, accAssignments) ->
                    let (BindIdent com accContext (newContext, ident)) = var
                    let assignment = Fabel.VarDeclaration (ident, binding, var.IsMutable)
                    newContext, (assignment::accAssignments)) decVars decBindings (ctx, [])
            assignments @ [transformExpr com newContext decBody]
            |> makeSequential (makeRangeFrom fsExpr)

    | BasicPatterns.DecisionTree(decisionExpr, decisionTargets) ->
        let rec getTargetRefsCount map = function
            | BasicPatterns.IfThenElse (_, thenExpr, elseExpr) ->
                let map = getTargetRefsCount map thenExpr
                getTargetRefsCount map elseExpr
            | BasicPatterns.DecisionTreeSuccess (idx, _) ->
                match (Map.tryFind idx map) with
                | Some refCount -> Map.remove idx map |> Map.add idx (refCount + 1)
                | None -> Map.add idx 1 map
            | _ as e ->
                failwithf "Unexpected DecisionTree branch in %A: %A" e.Range e
        let targetRefsCount = getTargetRefsCount (Map.empty<int,int>) decisionExpr
        // Convert targets referred more than once into functions
        // and just pass the F# implementation for the others
        let ctx, assignments =
            targetRefsCount
            |> Map.filter (fun k v -> v > 1)
            |> Map.fold (fun (ctx, acc) k v ->
                let decTargetVars, decTargetExpr = decisionTargets.[k]
                let lambda = makeLambda com ctx decTargetVars decTargetExpr
                let ctx, ident = makeSanitizedIdent ctx lambda.Type (sprintf "target%i" k)
                ctx, Map.add k (ident, lambda) acc) (ctx, Map.empty<_,_>)
        let decisionTargets =
            targetRefsCount |> Map.map (fun k v ->
                match v with
                | 1 -> TargetImpl decisionTargets.[k]
                | _ -> TargetRef (fst assignments.[k]))
        let ctx = { ctx with decisionTargets = decisionTargets }
        if assignments.Count = 0 then
            transformExpr com ctx decisionExpr
        else
            let assignments =
                assignments
                |> Seq.map (fun pair -> pair.Value)
                |> Seq.map (fun (ident, lambda) -> Fabel.VarDeclaration (ident, lambda, false))
                |> Seq.toList
            Fabel.Sequential (assignments @ [transformExpr com ctx decisionExpr], makeRangeFrom fsExpr)

    (** Not implemented *)
    | BasicPatterns.Quote _ // (quotedExpr)
    | BasicPatterns.AddressOf _ // (lvalueExpr)
    | BasicPatterns.AddressSet _ // (lvalueExpr, rvalueExpr)
    | _ -> failwithf "Cannot compile expression in %A: %A" fsExpr.Range fsExpr

// The F# compiler considers class methods as children of the enclosing module.
// We use this type to correct that, see type DeclInfo below.
type private EntChild =
    | Compiled of Fabel.Entity * SourceLocation
    | Ignored of fullName: string
    static member matchesFullName child str =
        match child with
        | Some (Compiled (ent, _)) -> ent.FullName = str
        | Some (Ignored fullName) -> fullName = str
        | None -> false

type private DeclInfo() =
    let mutable child: EntChild option = None
    let decls = ResizeArray<Fabel.Declaration>()
    let childDecls = ResizeArray<Fabel.Declaration>()
    let extMods = ResizeArray<Fabel.ExternalEntity>()
    // TODO: Check if constructor has Erase decorator
    /// Interface, inherits from System.Attribute, has "Erase" decorator...
    member self.IsIgnoredEntity (ent: FSharpEntity) =
        if ent.IsInterface then true else
        match ent.Attributes, ent.BaseType with
        | ContainsAtt "Erase" _, _ -> true
        | _, Some (NonAbbreviatedType t) when t.HasTypeDefinition ->
            match t.TypeDefinition.TryFullName with
            | Some "System.Attribute" -> true
            | _ -> false
        | _ -> false
    /// Is compiler generated or belongs to ignored entity?
    /// (remember F# compiler puts class methods in enclosing modules)
    member self.IsIgnoredMethod (meth: FSharpMemberOrFunctionOrValue) =
        if meth.IsCompilerGenerated then true else
        match child with
        | Some (Ignored fullName) ->
            (sanitizeEntityName meth.EnclosingEntity) = fullName
        | _ -> false
    member self.AddMethod (methDecl: Fabel.Declaration, parentName: string) =
        EntChild.matchesFullName child parentName
        |> function true -> childDecls.Add methDecl
                  | false -> self.ClearChild (); decls.Add methDecl
    member self.AddInitAction (actionDecl: Fabel.Declaration) =
        self.ClearChild ()
        decls.Add actionDecl
    member self.AddExternal (extMod: Fabel.ExternalEntity) =
        self.ClearChild ()
        child <- Some (Ignored extMod.FullName)
        extMods.Add extMod
    member self.ClearChild () =
        match child with
        | Some (Compiled (child, range)) ->
            Fabel.EntityDeclaration (child, List.ofSeq childDecls, range)
            |> decls.Add
        | _ -> ()
        child <- None
        childDecls.Clear ()
    member self.AddChild (newChild, newChildRange, newChildDecls, childExtMods) =
        self.ClearChild ()
        child <- Some (Compiled (newChild, newChildRange))
        childDecls.AddRange newChildDecls
        extMods.AddRange childExtMods
    member self.AddIgnored (ent: FSharpEntity) =
        self.ClearChild ()
        child <- Some (Ignored (sanitizeEntityName ent))
    member self.GetDeclarationsAndExternalModules () =
        self.ClearChild ()
        List.ofSeq decls, List.ofSeq extMods        
    
let private transformMemberDecl (com: IFabelCompiler) ctx (declInfo: DeclInfo)
    (meth: FSharpMemberOrFunctionOrValue) (args: FSharpMemberOrFunctionOrValue list list) (body: FSharpExpr) =
    if declInfo.IsIgnoredMethod meth |> not then
        let memberKind =
            let name = sanitizeMethodName com meth
            // TODO: Another way to check module values?
            if meth.EnclosingEntity.IsFSharpModule then
                match meth.XmlDocSig.[0] with
                | 'P' -> Fabel.Getter name
                | _ -> Fabel.Method name
            else
                // TODO: Check overloads
                if meth.IsImplicitConstructor then Fabel.Constructor
                elif meth.IsPropertyGetterMethod then Fabel.Getter name
                elif meth.IsPropertySetterMethod then Fabel.Setter name
                else Fabel.Method name
        let ctx, args =
            let args = if meth.IsInstanceMember then Seq.skip 1 args |> Seq.toList else args
            match args with
            | [] -> ctx, []
            | [[singleArg]] ->
                makeType com singleArg.FullType |> function
                | Fabel.PrimitiveType Fabel.Unit -> ctx, []
                | _ -> let (BindIdent com ctx (ctx, arg)) = singleArg in ctx, [arg]
            | _ ->
                List.foldBack (fun tupledArg (accContext, accArgs) ->
                    match tupledArg with
                    | [] -> failwith "Unexpected empty tupled in curried arguments"
                    | [nonTupledArg] ->
                        let (BindIdent com accContext (newContext, arg)) = nonTupledArg
                        newContext, arg::accArgs
                    | _ ->
                        // The F# compiler "untuples" the args in methods
                        let newContext, untupledArg = makeLambdaArgs com ctx tupledArg
                        newContext, untupledArg@accArgs
                ) args (ctx, []) // TODO: Reset Context?
        let entMember = 
            Fabel.Member(memberKind,
                makeRange meth.DeclarationLocation, args, transformExpr com ctx body,
                meth.Attributes |> Seq.choose (makeDecorator com) |> Seq.toList,
                meth.Accessibility.IsPublic, not meth.IsInstanceMember)
            |> Fabel.MemberDeclaration
        declInfo.AddMethod (entMember, sanitizeEntityName meth.EnclosingEntity)
    declInfo
   
let rec private transformEntityDecl
    (com: IFabelCompiler) ctx (declInfo: DeclInfo) (ent: FSharpEntity) subDecls =
    match ent.Attributes with
    | ContainsAtt "Global" _ ->
        Fabel.GlobalModule ent.FullName
        |> declInfo.AddExternal
        declInfo
    | ContainsAtt "Import" args ->
        match args with
        | [:? string as modName] when not(System.String.IsNullOrWhiteSpace modName) ->
            Fabel.ImportModule(ent.FullName, modName)
            |> declInfo.AddExternal
            declInfo
        | _ -> failwith "Import attributes must have a single non-empty string argument"
    | _ when declInfo.IsIgnoredEntity ent ->
        declInfo.AddIgnored ent
        declInfo
    | _ ->
        let ctx = { ctx with parentEntities = ent::ctx.parentEntities }
        let childDecls, childExtMods = transformDeclarations com ctx subDecls
        declInfo.AddChild (com.GetEntity ent, makeRange ent.DeclarationLocation, childDecls, childExtMods)
        declInfo

and private transformDeclarations (com: IFabelCompiler) ctx decls =
    let declInfo =
        decls |> List.fold (fun (declInfo: DeclInfo) decl ->
            match decl with
            | FSharpImplementationFileDeclaration.Entity (e, sub) ->
                transformEntityDecl com ctx declInfo e sub
            | FSharpImplementationFileDeclaration.MemberOrFunctionOrValue (meth, args, body) ->
                transformMemberDecl com ctx declInfo meth args body
            | FSharpImplementationFileDeclaration.InitAction (Transform com ctx expr) ->
                declInfo.AddInitAction (Fabel.ActionDeclaration expr); declInfo
        ) (DeclInfo())
    declInfo.GetDeclarationsAndExternalModules ()
        
let transformFiles (com: ICompiler) (fsProj: FSharpCheckProjectResults) =
    let emptyContext parent = {
        scope = []
        decisionTargets = Map.empty<_,_>
        parentEntities = match parent with Some p -> [p] | None -> [] 
    }
    let rec getRootDecls rootEnt = function
        | [FSharpImplementationFileDeclaration.Entity (e, subDecls)]
            when e.IsNamespace || e.IsFSharpModule ->
            getRootDecls (Some e) subDecls
        | _ as decls -> rootEnt, decls
    let entities =
        System.Collections.Concurrent.ConcurrentDictionary<string, Fabel.Entity>()
    let fileNames =
        fsProj.AssemblyContents.ImplementationFiles
        |> Seq.map (fun x -> x.FileName) |> Set.ofSeq
    let com =
        { new IFabelCompiler with
            member fcom.Transform ctx fsExpr =
                transformExpr fcom ctx fsExpr
            member fcom.GetInternalFile tdef =
                // In F# scripts the DeclarationLocation of referenced libraries
                // becomes the .fsx file, so check first if the entity belongs
                // to an assembly already compiled (external to the project)
                match tdef.Assembly.FileName with
                | Some _ -> None
                | None ->
                    let file = tdef.DeclarationLocation.FileName
                    if Set.contains file fileNames then Some file else None
            member fcom.GetEntity tdef =
                entities.GetOrAdd (tdef.FullName, fun _ -> makeEntity fcom tdef)
        interface ICompiler with
            member __.Options = com.Options }    
    fsProj.AssemblyContents.ImplementationFiles
    |> List.map (fun file ->
        let rootEnt, rootDecls = getRootDecls None file.Declarations
        let rootDecls, extDecls = transformDeclarations com (emptyContext rootEnt) rootDecls
        match rootEnt with
        | Some rootEnt -> makeEntity com rootEnt
        | None -> Fabel.Entity.CreateRootModule file.FileName
        |> fun rootEnt -> Fabel.File(file.FileName, rootEnt, rootDecls, extDecls))
