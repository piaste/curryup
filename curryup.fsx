﻿/// Generates curryable functional wrappers for .Net types.
namespace CurryUp

open System
open System.Reflection
open System.Text

[<AutoOpen>] 
module Configuration =

    type NamespaceTypeOrLibrary = string
    type Sourcefile             = string
    type Config = 
        { From             : NamespaceTypeOrLibrary 
          To               : Sourcefile
          MethodOverload   : string -> string
          CurriedNamespace : string -> string }


[<AutoOpen>] 
module private Shared =

    let instance (m:MethodBase) = (not <| m.IsStatic)
    let isVoid (m:MethodBase) = (m :?> MethodInfo).ReturnType = typeof<Void>
    let iff b v = if b(v) then Some v else None 
    let (|SourceFile|_|)     = iff (fun (f:string) -> f.EndsWith(".fs") || f.EndsWith(".fsx"))
    let (|Library|_|)        = iff (fun (d:string) -> d.EndsWith(".dll"))
    let (|IsOutParam|_|)     = iff (fun (p:ParameterInfo) -> p.IsOut && not (p.IsIn))
    let (|IsGenericParam|_|) = iff (fun (p:ParameterInfo) -> p.ParameterType.IsGenericType)
    let (|IsStaticProp|_|)   = iff (fun (p:PropertyInfo) -> p.GetGetMethod().IsStatic )
    let (|IsConstructor|_|)  = iff (fun (m:MethodBase) -> m.MemberType = MemberTypes.Constructor)
    let (|IsStatic|_|)       = iff (fun (m:MethodBase) -> m.IsStatic)
    let (|HasParams|_|)      = iff (fun (m:MethodBase) -> m.GetParameters().Length <> 0)
    let (|IsInstanceVoid|_|) = iff (fun (m:MethodBase) -> m |> instance && m |> isVoid)
    let (|IsGeneric|_|)      = iff (fun (t:Type) -> t.IsGenericType || t.Name.Contains("`"))
    let (|HasGenericName|_|) = function
            | IsGeneric t -> Some t
            | t when t.FullName |> isNull -> Some t
            | t when t.FullName = "T[]" -> Some t
            | _ -> None
    let fmt format = sprintf format
    let trimTo (char:string) (input:string) = if input.Contains(char) then input.Substring(0, input.IndexOf(char)) else input
    let kill (char:string) (input:string) = input.Replace(char, "")
    let trimGeneric = trimTo "`"
    let tickEscape s = "'" + s
    let tick s = s + "'"
    let safeChars = kill "&" >> kill "*"
    let lcaseFirstLetter = function | "" -> "" | s -> s.Substring(0,1).ToLower() + s.Substring(1)
    let methName = function | IsConstructor _ -> "new" | m -> m.Name |> lcaseFirstLetter
    let naughtyNames = [| "val"; "yield"; "use"; "type"; "to"; "then"; "select"; "rec"; 
                        "open"; "or"; "namespace"; "module"; "match"; "inline"; "inherit";
                        "function"; "func"; "params"; "end"; "done"; "begin"; "assert"; |]
    let isNaughty name = naughtyNames |> Array .contains name
    let cleanName name = if name |> isNaughty then tick name else name


module private Namespace =
    
    [<AutoOpen>] 
    module private shh =
        let assemblies () = AppDomain.CurrentDomain.GetAssemblies()
        let isIn namespace' type' = (type':Type).IsClass && type'.IsPublic && not (type'.IsAbstract) && type'.Namespace = namespace'
        let isTypeName namespace' type' = (type':Type).FullName = namespace'
        let matches namespace' type' = type' |> isIn namespace' || type' |> isTypeName namespace'
        let forNamespace namespace' = Seq.filter (namespace' |> matches)
        let types' ass = (ass:Assembly).GetTypes()
        let typesIn namespace' = types' >> forNamespace namespace'
        let findTypesIn namespace' = Seq.collect <| typesIn namespace'
        let name type' = (type':Type).FullName
        let findNames = Seq.map name >> Seq.toList
        
    let types namespace' = assemblies() |> findTypesIn namespace'
    let typeNames namespace' = namespace' |> (types >> findNames)


module private Type' =  

    [<AutoOpen>] 
    module private shh =
        let complianceAttributes (m:MethodInfo) = m.GetCustomAttributes<System.CLSCompliantAttribute>() |> Seq.toList
        let isClsCompliant (m:MethodInfo) =
            match m |> complianceAttributes with
            | [] -> true
            | atts -> atts |> List.fold (fun acc a -> acc && a.IsCompliant) true
        let getMethods (t:Type) = t.GetMethods() |> Array.filter isClsCompliant
        let isGetterOrSetter (m:MethodBase) = m.Name.StartsWith("get_") || m.Name.StartsWith("set_")
        let isMethod = not << isGetterOrSetter
        let noProps = List.filter isMethod
        let toMethodBase = Seq.cast<MethodBase>
        let typeMethods (t:Type) = t|> (getMethods >> toMethodBase)
        let sortByName = Seq.sortBy methName >> Seq.toList
        let allMethods (t:Type) = t|> (typeMethods >> sortByName)
        let methods (t:Type) = t|> (allMethods >> noProps)
        let overload escape (prevOrig,prevName) name = if name = prevOrig then escape prevName else name |> cleanName
        let makeOverloads escape (prev,acc)  meth =
            let name, finalName = meth |> methName, overload escape prev (meth |> methName)
            ((name, finalName), acc @ [(finalName, meth)])
        let overloadAcc = (("",""), [])
        let customEscape escape (methods: MethodBase list) =
            methods |> List.fold (makeOverloads escape) overloadAcc |> snd
        let escapeNames (config:Config) = customEscape config.MethodOverload

    let methods (config:Config) t = t |> (methods >> escapeNames config)
    let constructors (t:Type) = t.GetConstructors()


module private Generate =

    [<AutoOpen>]
    module private utility =

        let (||>) s sb = (sb:StringBuilder).AppendLine s |> ignore
        let write writeTo = 
            let sb = new StringBuilder()
            writeTo sb
            sb.ToString()
        let (++) x y = x + y
        let name (t:Type) = t.Name
        let safeName (t:Type) = sprintf "%s.%s" t.Namespace t.Name

    [<AutoOpen>]
    module private generic =

        let typeDef = function | t when (t:Type).IsGenericType -> t.GetGenericTypeDefinition() | t -> t
        let genericArgs (t:Type) = t.GetGenericArguments()
        let typeArgs (t:Type) = 
            t 
            |> (typeDef >> genericArgs)
            |> Seq.map (name >> tickEscape)
            |> String.concat ","
        let typeContraints (t:Type) =
            t.GetGenericTypeDefinition()
             .GetGenericArguments()
            |> Array.map (fun a -> a.GetGenericParameterConstraints())
        let typeNameWithContraints t (name:string) = 
            let constraints = ""
            let array = if (name.EndsWith("[]")) then "[]" else "" 
            sprintf "%s<%s%s>%s" (name |> trimGeneric) (t |> typeArgs) constraints array
        let typeName t name = sprintf "%s<%s>" (name |> trimGeneric) (t |> typeArgs)
        let typeFullName t = typeName t t.FullName

    [<AutoOpen>]
    module private type' =

        let fullname = function
            | IsGeneric t -> t |> generic.typeFullName
            | t -> t.FullName
        let name  = function
            | IsGeneric t -> t.Name |> trimGeneric
            | t -> t.Name        
        let isEquatable (t:Type) = (t |> safeName) = "System.Collections.Generic.IEqualityComparer`1"
        let nameOfGenericParam t = t |> (safeName >> generic.typeNameWithContraints t)
        let nameOfTypeParam = name >> (safeChars >> tickEscape)
        let fullTypeName = function 
            | HasGenericName t -> 
                match t with
                | IsGeneric t -> t |> nameOfGenericParam
                | t           -> t |> nameOfTypeParam
            | t -> t.FullName
        let isBool p = (p:PropertyInfo).PropertyType = typeof<bool>
        let boolProps (t:Type) = t.GetProperties() |> Array.filter isBool
        let patt = function
            | IsStaticProp p -> fmt "    let (|%s|_|) = if %s.%s then Some () else None" p.Name (p.DeclaringType |> fullname) p.Name 
            | p ->              fmt "    let (|%s|_|) this = if (this:%s).%s then Some this else None" p.Name (p.DeclaringType |> fullname) p.Name
        let activePatterns (t:Type) (write:StringBuilder) = 
            let write s = s ||> write
            let writePatterns = Seq.map (patt >> write)
            t |> (boolProps >> writePatterns) |> ignore

    [<AutoOpen>]
    module private param' =
          
        let typeName (p:ParameterInfo) = p.ParameterType |> fullTypeName |> safeChars
        let name (p:ParameterInfo) = p.Name |> cleanName
        let call = function
            | IsOutParam p -> sprintf "ref(%s)" (p |> name)
            | p            -> p |> name
        let argumentName p = sprintf "(%s:%s)" (p |> name) (p |> typeName)
        let map (map:ParameterInfo -> string) concatWith (m:MethodBase) =  
            m.GetParameters() |> Array.map map |> String.concat concatWith
        let curriedArgs  = function
            | HasParams m -> m |> map argumentName " "
            | IsStatic _ -> "() "
            | _ -> ""
        let callArgs = map call ", "
             
    [<AutoOpen>]
    module private method' =

        let constructorCall t = "", ""
        let staticCall t = "", t |> type'.fullname
        let curriedCall t = sprintf "fun (this:%s) -> " (t |> type'.fullname), "this"
        let memberCall t = function
            | IsConstructor _ -> t |> constructorCall
            | IsStatic _      -> t |> staticCall
            | _               -> t |> curriedCall
        let paramsSpace = function HasParams _ -> " " | _ -> ""
        let methodReturn = function IsInstanceVoid _ -> "; this" | _ -> ""

        let methodDeclaration t write ((name, m):string * MethodBase) =
                let call, instName = m |> memberCall t
                let sig'    = fmt "    let %s " name
                let params' = fmt "%s" (m |> curriedArgs) 
                let eq      = fmt "%s= " (m |> paramsSpace)
                let ref'    = fmt "%s.%s" instName m.Name
                let args    = fmt "(%s)%s" (m |> callArgs) (m |> methodReturn)

                sig' ++ params' ++ eq ++ call ++ ref' ++ args ||> write

        let curried (config:Config) (t:Type) write =
            fmt "/// Autogenerated curry-wrapper for %s\r\nmodule %s =" (t |> type'.fullname) (t |> type'.name) ||> write
            let writeMethods = methodDeclaration t write
            t |> Type'.methods config |> List.map writeMethods |> ignore
            sprintf "" ||> write

    let type' config t = t |> (curried config >> write)

    [<AutoOpen>]
    module private namespace' =

        let namespaceModule (config:Config) namespace' write =
            let header namespace' =
                sprintf "namespace %s\r\n\r\n" (namespace' |> trimGeneric |> config.CurriedNamespace) ||> write
                namespace'
            let types = Namespace.types
            let bodies types = 
                for t in types do curried config t write;
                types
            let patterns types = for t in types do activePatterns t write

            namespace' |> (header >> types >> bodies >> patterns) 

    let namespace' config = namespaceModule config >> write


/// Creates curryable wrappers for POCO's in a given namespace or assembly
module Curry =

    /// Default configuration for code generation, allows customization
    let DefaultConfig = 
        { From = "System.Collections.Generic" 
          To   = "stdout" 
          MethodOverload   = fun name -> name + "'"
          CurriedNamespace = fun namespace' -> namespace' + ".Curried" }

    [<AutoOpen>]
    module private shh =
        let load = function
            | Library path -> 
                query { for t in Assembly.LoadFrom(path).GetTypes() do
                        groupBy t.Namespace into g
                        select g.Key } |> Seq.toList
            | from' -> [ from' ]
        let generate config = 
            let generation = Generate.namespace' config
            (List.map generation) >> String.concat ""
        let write out = function
            | SourceFile file ->
                    System.IO.File.WriteAllText (file, out)
                    sprintf "Curried output written to: %s" file
            | _ -> out

    /// Generates curryable wrappers from the provided configuration
    let up' (config:Config) = 
        let namespaces = load config.From
        let out = namespaces |> generate config
        write out config.To

    /// Generates curryable wrappers to the provided location from a given library path or namespace/type name
    let up (to':string) (from':string) = up' { DefaultConfig with To = to'; From = from' }