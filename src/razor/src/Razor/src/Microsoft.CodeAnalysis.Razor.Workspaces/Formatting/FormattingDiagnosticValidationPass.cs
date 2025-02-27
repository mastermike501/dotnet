﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT license. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Razor.Language;
using Microsoft.CodeAnalysis.Razor.DocumentMapping;
using Microsoft.CodeAnalysis.Razor.Logging;
using Microsoft.CodeAnalysis.Razor.Protocol;
using Microsoft.VisualStudio.LanguageServer.Protocol;

namespace Microsoft.CodeAnalysis.Razor.Formatting;

internal sealed class FormattingDiagnosticValidationPass(
    IDocumentMappingService documentMappingService,
    ILoggerFactory loggerFactory)
    : FormattingPassBase(documentMappingService)
{
    private readonly ILogger _logger = loggerFactory.GetOrCreateLogger<FormattingDiagnosticValidationPass>();

    // We want this to run at the very end.
    public override int Order => DefaultOrder + 1000;

    public override bool IsValidationPass => true;

    // Internal for testing.
    internal bool DebugAssertsEnabled { get; set; } = true;

    public async override Task<FormattingResult> ExecuteAsync(FormattingContext context, FormattingResult result, CancellationToken cancellationToken)
    {
        if (result.Kind != RazorLanguageKind.Razor)
        {
            // We don't care about changes to projected documents here.
            return result;
        }

        var originalDiagnostics = context.CodeDocument.GetSyntaxTree().Diagnostics;

        var text = context.SourceText;
        var edits = result.Edits;
        var changes = edits.Select(text.GetTextChange);
        var changedText = text.WithChanges(changes);
        var changedContext = await context.WithTextAsync(changedText).ConfigureAwait(false);
        var changedDiagnostics = changedContext.CodeDocument.GetSyntaxTree().Diagnostics;

        // We want to ensure diagnostics didn't change, but since we're formatting things, its expected
        // that some of them might have moved around.
        // This is not 100% correct, as the formatting technically could still cause a compile error,
        // but only if it also fixes one at the same time, so its probably an edge case (if indeed it's
        // at all possible). Also worth noting the order has to be maintained in that case.
        if (!originalDiagnostics.SequenceEqual(changedDiagnostics, LocationIgnoringDiagnosticComparer.Instance))
        {
            _logger.LogWarning($"{SR.Format_operation_changed_diagnostics}");
            _logger.LogWarning($"{SR.Diagnostics_before}");
            foreach (var diagnostic in originalDiagnostics)
            {
                _logger.LogWarning($"{diagnostic}");
            }

            _logger.LogWarning($"{SR.Diagnostics_after}");
            foreach (var diagnostic in changedDiagnostics)
            {
                _logger.LogWarning($"{diagnostic}");
            }

            if (DebugAssertsEnabled)
            {
                Debug.Fail("A formatting result was rejected because the formatted text produced different diagnostics compared to the original text.");
            }

            return new FormattingResult([]);
        }

        return result;
    }

    private class LocationIgnoringDiagnosticComparer : IEqualityComparer<RazorDiagnostic>
    {
        public static IEqualityComparer<RazorDiagnostic> Instance = new LocationIgnoringDiagnosticComparer();

        public bool Equals(RazorDiagnostic? x, RazorDiagnostic? y)
            => x is not null &&
                y is not null &&
                x.Severity == y.Severity &&
                x.Id == y.Id;

        public int GetHashCode(RazorDiagnostic obj)
            => obj.GetHashCode();
    }
}
