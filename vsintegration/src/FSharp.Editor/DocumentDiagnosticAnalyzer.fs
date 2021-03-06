﻿// Copyright (c) Microsoft Corporation.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

namespace Microsoft.VisualStudio.FSharp.Editor

open System
open System.Composition
open System.Collections.Immutable
open System.Threading
open System.Threading.Tasks

open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.Diagnostics
open Microsoft.CodeAnalysis.Host.Mef
open Microsoft.CodeAnalysis.Text
open Microsoft.CodeAnalysis.SolutionCrawler

open Microsoft.FSharp.Compiler
open Microsoft.FSharp.Compiler.SourceCodeServices
open Microsoft.FSharp.Compiler.Range

open Microsoft.VisualStudio.FSharp.LanguageService

[<DiagnosticAnalyzer(FSharpCommonConstants.FSharpLanguageName)>]
type internal FSharpDocumentDiagnosticAnalyzer() =
    inherit DocumentDiagnosticAnalyzer()

    static member GetDiagnostics(filePath: string, sourceText: SourceText, textVersionHash: int, options: FSharpProjectOptions, addSemanticErrors: bool) = async {


        let! parseResults = FSharpLanguageService.Checker.ParseFileInProject(filePath, sourceText.ToString(), options) 
        let! errors = async {
            if addSemanticErrors then
                let! checkResultsAnswer = FSharpLanguageService.Checker.CheckFileInProject(parseResults, filePath, textVersionHash, sourceText.ToString(), options) 
                match checkResultsAnswer with
                | FSharpCheckFileAnswer.Aborted -> return [| |]
                | FSharpCheckFileAnswer.Succeeded(results) -> return results.Errors
            else
                return parseResults.Errors
          }
        
        let results = 
          (errors |> Seq.choose(fun (error) ->
            if error.StartLineAlternate = 0 || error.EndLineAlternate = 0 then
                // F# error line numbers are one-based. Compiler returns 0 for global errors (reported by ProjectDiagnosticAnalyzer)
                None
            else
                // Roslyn line numbers are zero-based
                let linePositionSpan = LinePositionSpan(LinePosition(error.StartLineAlternate - 1, error.StartColumn),LinePosition(error.EndLineAlternate - 1, error.EndColumn))
                let textSpan = sourceText.Lines.GetTextSpan(linePositionSpan)
                // F# compiler report errors at end of file if parsing fails. It should be corrected to match Roslyn boundaries
                let correctedTextSpan = if textSpan.End < sourceText.Length then textSpan else TextSpan.FromBounds(max 0 (sourceText.Length - 1), sourceText.Length)
                let location = Location.Create(filePath, correctedTextSpan , linePositionSpan)
                Some(CommonRoslynHelpers.ConvertError(error, location)))
          ).ToImmutableArray()
        return results
      }

    override this.SupportedDiagnostics = CommonRoslynHelpers.SupportedDiagnostics()

    override this.AnalyzeSyntaxAsync(document: Document, cancellationToken: CancellationToken): Task<ImmutableArray<Diagnostic>> =
        async {
            match FSharpLanguageService.TryGetOptionsForEditingDocumentOrProject(document)  with 
            | Some options ->
                let! sourceText = document.GetTextAsync(cancellationToken) |> Async.AwaitTask
                let! textVersion = document.GetTextVersionAsync(cancellationToken) |> Async.AwaitTask
                return! FSharpDocumentDiagnosticAnalyzer.GetDiagnostics(document.FilePath, sourceText, textVersion.GetHashCode(), options, false)
            | None -> return ImmutableArray<Diagnostic>.Empty
        } |> CommonRoslynHelpers.StartAsyncAsTask cancellationToken


    override this.AnalyzeSemanticsAsync(document: Document, cancellationToken: CancellationToken): Task<ImmutableArray<Diagnostic>> =
        async {
            let! optionsOpt = FSharpLanguageService.TryGetOptionsForDocumentOrProject(document) 
            match optionsOpt with 
            | Some options ->
                let! sourceText = document.GetTextAsync(cancellationToken) |> Async.AwaitTask
                let! textVersion = document.GetTextVersionAsync(cancellationToken) |> Async.AwaitTask
                return! FSharpDocumentDiagnosticAnalyzer.GetDiagnostics(document.FilePath, sourceText, textVersion.GetHashCode(), options, true)
            | None -> return ImmutableArray<Diagnostic>.Empty
        } |> CommonRoslynHelpers.StartAsyncAsTask cancellationToken

