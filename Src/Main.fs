﻿namespace XTract

open CsvHelper
open Fizzler.Systems.HtmlAgilityPack
open HtmlAgilityPack
open Microsoft.FSharp.Reflection
open Newtonsoft.Json
open SpreadSharp
open System
open System.Collections.Generic
open System.IO
open System.Text.RegularExpressions
open System.Collections.Concurrent

/// A type for describing a property to scrape.
/// Fields:
/// selector: CSS query selector,
/// pattern: regex pattern to match against the selected element's inner HTML,
/// attributes: the HTML attributes to scrape from the selected element.
type Extractor = 
    {
        selector : string
        pattern : string
        attributes : string list
    }
    
    static member New selector = 
        {
            selector = selector
            pattern = "^()(.*?)()$"
            attributes = [ "text" ]
        }
    
    static member WithPattern pattern property =
        {
            property with
                pattern = pattern
        }

    static member WithAttributes attributes property =
        {
            property with
                attributes = attributes
        }

module private Utils = 
    let private noneAttrs attrs (dictionary : Dictionary<string, string>) = 
        attrs
        |> List.iter (fun x ->
            let key = dictionary.Count + 1 |> string
            match x with
            | "text" -> dictionary.Add(key + "-text", "")
            | _ -> dictionary.Add(key + "-" + x, ""))
    
    let private someAttrs attrs extractor (htmlNode : HtmlNode) (dictionary : Dictionary<string, string>) = 
        attrs
        |> List.iter (fun x -> 
            let key = dictionary.Count + 1 |> string
            match x with
            | "text" -> 
                let text = 
                    let ``match`` = Regex(extractor.pattern).Match(htmlNode.InnerText)
                    match ``match``.Success with
                    | false -> ""
                    | true -> ``match``.Groups.[2].Value
                dictionary.Add(key + "-text", text)
            | _ -> dictionary.Add(key + "-" + x, htmlNode.GetAttributeValue(x, "")))
    
    let extract extractor htmlNode dictionary = 
        let attrs = extractor.attributes
        match htmlNode with
        | None -> noneAttrs attrs dictionary
        | Some htmlNode -> someAttrs attrs extractor htmlNode dictionary
    
    let rec extractAll (acc : Dictionary<string, string> list) (enums : (Extractor * IEnumerator<HtmlNode> option) list) = 
        let enums' = 
            enums |> List.map (fun (extrator, enumerator) -> 
                         match enumerator with
                         | None -> extrator, enumerator, None
                         | Some x -> 
                             let htmlNode = 
                                 match x.MoveNext() with
                                 | false -> None
                                 | true -> Some x.Current
                             extrator, enumerator, htmlNode)
        match enums' |> List.exists (fun (_, _, htmlNode) -> htmlNode.IsSome) with
        | false -> acc
        | true -> 
            let acc' = 
                [ let dictionary = Dictionary<string, string>()
                  for (extractor, _, htmlNode) in enums' do
                      extract extractor htmlNode dictionary
                  yield dictionary ]
            enums'
            |> List.map (fun (extractor, enumerator, _) -> extractor, enumerator)
            |> extractAll (acc @ acc')

    let (|Html|Url|) input =
        match Regex("(ht|f)tp(s?)\:\/\/[0-9a-zA-Z]([-.\w]*[0-9a-zA-Z])*(:(0-9)*)*(\/?)([a-zA-Z0-9\-\.\?\,\'\/\\\+&amp;%\$#_]*)?")
            .IsMatch(input) with
        | false -> Html
        | true -> Url

    let scrape<'T> html extractors (dataStore:ConcurrentBag<'T>) =
        let doc = HtmlDocument()
        doc.LoadHtml html
        let root = doc.DocumentNode
        let dictionary = Dictionary<string, string>()
        extractors
        |> List.map (fun x -> 
                let selection = root.QuerySelector(x.selector)
                match selection with
                | null -> x, None
                | _ -> x, Some selection)
        |> List.iter (fun (x, htmlNode) -> extract x htmlNode dictionary)
        let record = 
            let arr = 
                dictionary.Values
                |> Seq.toArray
                |> Array.map box
            FSharpValue.MakeRecord(typeof<'T>, arr) :?> 'T
        dataStore.Add record
        Some record
    
    let scrapeAll<'T> html extractors (dataStore:ConcurrentBag<'T>) =
        let doc = HtmlDocument()
        doc.LoadHtml html
        let root = doc.DocumentNode
        
        let enums = 
            [ for x in extractors -> 
                    let selection = root.QuerySelectorAll(x.selector)
                    match selection with
                    | null -> x, None
                    | _ -> x, Some <| selection.GetEnumerator() ]
        extractAll [] enums |> List.map (fun x -> 
                                            let record = 
                                                let arr = 
                                                    x.Values
                                                    |> Seq.toArray
                                                    |> Array.map box
                                                FSharpValue.MakeRecord(typeof<'T>, arr) :?> 'T
                                            dataStore.Add record
                                            record) |> Some

type Scraper<'T>(extractors) = 
    let dataStore = ConcurrentBag<'T>()
    let log = ConcurrentQueue<string>()
    let mutable loggingEnabled = true

    let logger =
        MailboxProcessor.Start(fun inbox ->
            let rec loop() =
                async {
                    let! msg = inbox.Receive()
                    let time = DateTime.Now.ToString()
                    let msg' = time + " - " + msg
                    log.Enqueue msg'
                    match loggingEnabled with
                    | false -> ()
                    | true -> printfn "%s" msg'
                    return! loop()
                }
            loop()
        )
    
    member __.Log msg = logger.Post msg

    member __.LogData() = log.ToArray()

    member __.WithLogging enabled = loggingEnabled <- enabled

    member __.Scrape input =
        match input with
        | Utils.Html ->
            let substring =
                match input.Length with
                | l when l < 50 -> input + "..."
                | _ -> input.Substring(0, 50) + "..."
            logger.Post <| "Scraping " + substring
            Utils.scrape<'T> input extractors dataStore

        | Utils.Url ->
            logger.Post <| "Downloading " + input
            let html = Http.get input
            match html with
            | None ->
                logger.Post <| "Failed to get " + input
                None
            | Some html ->
                logger.Post <| "Scraping " + input
                Utils.scrape<'T> html extractors dataStore

    member __.ScrapeAll input = 
        try
            match input with
            | Utils.Html ->
                let substring =
                    match input.Length with
                    | l when l < 50 -> input + "..."
                    | _ -> input.Substring(0, 50) + "..."
                logger.Post <| "Scraping " + substring                
                Utils.scrapeAll<'T> input extractors dataStore
            | Utils.Url ->
                let html = Http.get input
                match html with
                | None ->
                    logger.Post <| "Failed to get " + input
                    None
                | Some html ->
                    logger.Post <| "Scraping " + input                    
                    Utils.scrapeAll<'T> html extractors dataStore
        with _ -> None

    member __.Data() = dataStore.ToArray()

    member __.JsonData() =
        dataStore.ToArray()
        |> fun x -> JsonConvert.SerializeObject(x, Formatting.Indented)

    member __.SaveCsv(path) =
        let sw = File.CreateText(path)
        let csv = new CsvWriter(sw)
        csv.WriteRecords(dataStore)
        sw.Flush()
        sw.Dispose()

    member __.SaveExcel(path) =
        Records.saveAs dataStore typeof<'T> path