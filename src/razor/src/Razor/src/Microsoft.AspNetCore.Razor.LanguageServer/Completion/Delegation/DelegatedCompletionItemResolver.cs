﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT license. See License.txt in the project root for license information.

using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Razor.LanguageServer.Formatting;
using Microsoft.AspNetCore.Razor.LanguageServer.Hosting;
using Microsoft.CodeAnalysis.Razor.Formatting;
using Microsoft.CodeAnalysis.Razor.ProjectSystem;
using Microsoft.CodeAnalysis.Razor.Protocol;
using Microsoft.VisualStudio.LanguageServer.Protocol;

namespace Microsoft.AspNetCore.Razor.LanguageServer.Completion.Delegation;

internal class DelegatedCompletionItemResolver : CompletionItemResolver
{
    private readonly IDocumentContextFactory _documentContextFactory;
    private readonly IRazorFormattingService _formattingService;
    private readonly IClientConnection _clientConnection;

    public DelegatedCompletionItemResolver(
        IDocumentContextFactory documentContextFactory,
        IRazorFormattingService formattingService,
        IClientConnection clientConnection)
    {
        _documentContextFactory = documentContextFactory;
        _formattingService = formattingService;
        _clientConnection = clientConnection;
    }

    public override async Task<VSInternalCompletionItem?> ResolveAsync(
        VSInternalCompletionItem item,
        VSInternalCompletionList containingCompletionList,
        object? originalRequestContext,
        VSInternalClientCapabilities? clientCapabilities,
        CancellationToken cancellationToken)
    {
        if (originalRequestContext is not DelegatedCompletionResolutionContext resolutionContext)
        {
            // Can't recognize the original request context, bail.
            return null;
        }

        var labelQuery = item.Label;
        var associatedDelegatedCompletion = containingCompletionList.Items.FirstOrDefault(completion => string.Equals(labelQuery, completion.Label, StringComparison.Ordinal));
        if (associatedDelegatedCompletion is null)
        {
            return null;
        }

        // If the data was merged to combine resultId with original data, undo that merge and set the data back
        // to what it originally was for the delegated request
        if (CompletionListMerger.TrySplit(associatedDelegatedCompletion.Data, out var splitData) && splitData.Length == 2)
        {
            item.Data = splitData[1];
        }
        else
        {
            item.Data = associatedDelegatedCompletion.Data ?? resolutionContext.OriginalCompletionListData;
        }

        var delegatedParams = resolutionContext.OriginalRequestParams;
        var delegatedResolveParams = new DelegatedCompletionItemResolveParams(
            delegatedParams.Identifier,
            item,
            delegatedParams.ProjectedKind);
        var resolvedCompletionItem = await _clientConnection.SendRequestAsync<DelegatedCompletionItemResolveParams, VSInternalCompletionItem?>(CodeAnalysis.Razor.Protocol.LanguageServerConstants.RazorCompletionResolveEndpointName, delegatedResolveParams, cancellationToken).ConfigureAwait(false);

        if (resolvedCompletionItem is not null)
        {
            resolvedCompletionItem = await PostProcessCompletionItemAsync(resolutionContext, resolvedCompletionItem, cancellationToken).ConfigureAwait(false);
        }

        return resolvedCompletionItem;
    }

    private async Task<VSInternalCompletionItem> PostProcessCompletionItemAsync(
        DelegatedCompletionResolutionContext context,
        VSInternalCompletionItem resolvedCompletionItem,
        CancellationToken cancellationToken)
    {
        if (context.OriginalRequestParams.ProjectedKind != RazorLanguageKind.CSharp)
        {
            // We currently don't do any post-processing for non-C# items.
            return resolvedCompletionItem;
        }

        if (!resolvedCompletionItem.VsResolveTextEditOnCommit)
        {
            // Resolve doesn't typically handle text edit resolution; however, in VS cases it does.
            return resolvedCompletionItem;
        }

        if (resolvedCompletionItem.TextEdit is null && resolvedCompletionItem.AdditionalTextEdits is null)
        {
            // Only post-processing work we have to do is formatting text edits on resolution.
            return resolvedCompletionItem;
        }

        var identifier = context.OriginalRequestParams.Identifier.TextDocumentIdentifier;
        if (!_documentContextFactory.TryCreateForOpenDocument(identifier, out var documentContext))
        {
            return resolvedCompletionItem;
        }

        var formattingOptions = await _clientConnection
            .SendRequestAsync<TextDocumentIdentifierAndVersion, FormattingOptions?>(
                LanguageServerConstants.RazorGetFormattingOptionsEndpointName,
                documentContext.GetTextDocumentIdentifierAndVersion(),
                cancellationToken)
            .ConfigureAwait(false);

        if (formattingOptions is null)
        {
            return resolvedCompletionItem;
        }

        if (resolvedCompletionItem.TextEdit is not null)
        {
            if (resolvedCompletionItem.TextEdit.Value.TryGetFirst(out var textEdit))
            {
                var formattedTextEdit = await _formattingService.FormatSnippetAsync(
                    documentContext,
                    RazorLanguageKind.CSharp,
                    new[] { textEdit },
                    formattingOptions,
                    cancellationToken).ConfigureAwait(false);

                resolvedCompletionItem.TextEdit = formattedTextEdit.FirstOrDefault();
            }
            else
            {
                // TO-DO: Handle InsertReplaceEdit type
                // https://github.com/dotnet/razor/issues/8829
                Debug.Fail("Unsupported edit type.");
            }
        }

        if (resolvedCompletionItem.AdditionalTextEdits is not null)
        {
            var formattedTextEdits = await _formattingService.FormatSnippetAsync(
                documentContext,
                RazorLanguageKind.CSharp,
                resolvedCompletionItem.AdditionalTextEdits,
                formattingOptions,
                cancellationToken).ConfigureAwait(false);

            resolvedCompletionItem.AdditionalTextEdits = formattedTextEdits;
        }

        return resolvedCompletionItem;
    }
}
