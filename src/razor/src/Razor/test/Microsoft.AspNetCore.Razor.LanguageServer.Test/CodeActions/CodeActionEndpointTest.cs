﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT license. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Razor.Language;
using Microsoft.AspNetCore.Razor.LanguageServer.CodeActions.Models;
using Microsoft.AspNetCore.Razor.LanguageServer.Hosting;
using Microsoft.AspNetCore.Razor.Test.Common.LanguageServer;
using Microsoft.AspNetCore.Razor.Threading;
using Microsoft.CodeAnalysis.Razor.DocumentMapping;
using Microsoft.CodeAnalysis.Razor.Protocol;
using Microsoft.CodeAnalysis.Razor.Protocol.CodeActions;
using Microsoft.CodeAnalysis.Razor.Workspaces;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Moq;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.AspNetCore.Razor.LanguageServer.CodeActions;

public class CodeActionEndpointTest : LanguageServerTestBase
{
    private readonly IDocumentMappingService _documentMappingService;
    private readonly LanguageServerFeatureOptions _languageServerFeatureOptions;
    private readonly IClientConnection _clientConnection;

    public CodeActionEndpointTest(ITestOutputHelper testOutput)
        : base(testOutput)
    {
        _documentMappingService = Mock.Of<IDocumentMappingService>(
            s => s.TryMapToGeneratedDocumentRange(
                It.IsAny<IRazorGeneratedDocument>(),
                It.IsAny<LinePositionSpan>(),
                out It.Ref<LinePositionSpan>.IsAny) == false &&

                s.GetLanguageKind(It.IsAny<RazorCodeDocument>(), It.IsAny<int>(), It.IsAny<bool>()) == RazorLanguageKind.CSharp,

            MockBehavior.Strict);

        _languageServerFeatureOptions = Mock.Of<LanguageServerFeatureOptions>(
            l => l.SupportsFileManipulation == true,
            MockBehavior.Strict);

        _clientConnection = Mock.Of<IClientConnection>(MockBehavior.Strict);
    }

    [Fact]
    public async Task Handle_NoDocument()
    {
        // Arrange
        var documentPath = new Uri("C:/path/to/Page.razor");
        var codeActionEndpoint = new CodeActionEndpoint(
            _documentMappingService,
            Array.Empty<IRazorCodeActionProvider>(),
            Array.Empty<ICSharpCodeActionProvider>(),
            Array.Empty<IHtmlCodeActionProvider>(),
            _clientConnection,
            _languageServerFeatureOptions,
            LoggerFactory,
            telemetryReporter: null)
        {
            _supportsCodeActionResolve = false
        };
        var request = new VSCodeActionParams()
        {
            TextDocument = new VSTextDocumentIdentifier { Uri = documentPath },
            Range = VsLspFactory.CreateZeroWidthRange(0, 1),
            Context = new VSInternalCodeActionContext()
        };

        var requestContext = CreateRazorRequestContext(documentContext: null);

        // Act
        var commandOrCodeActionContainer = await codeActionEndpoint.HandleRequestAsync(request, requestContext, default);

        // Assert
        Assert.Null(commandOrCodeActionContainer);
    }

    [Fact]
    public async Task Handle_UnsupportedDocument()
    {
        // Arrange
        var documentPath = new Uri("C:/path/to/Page.razor");
        var codeDocument = CreateCodeDocument("@code {}");
        var documentContext = CreateDocumentContext(documentPath, codeDocument);
        codeDocument.SetUnsupported();
        var codeActionEndpoint = new CodeActionEndpoint(
            _documentMappingService,
            Array.Empty<IRazorCodeActionProvider>(),
            Array.Empty<ICSharpCodeActionProvider>(),
            Array.Empty<IHtmlCodeActionProvider>(),
            _clientConnection,
            _languageServerFeatureOptions,
            LoggerFactory,
            telemetryReporter: null)
        {
            _supportsCodeActionResolve = false
        };
        var request = new VSCodeActionParams()
        {
            TextDocument = new VSTextDocumentIdentifier { Uri = documentPath },
            Range = VsLspFactory.CreateZeroWidthRange(0, 1),
            Context = new VSInternalCodeActionContext()
        };
        var requestContext = CreateRazorRequestContext(documentContext);

        // Act
        var commandOrCodeActionContainer = await codeActionEndpoint.HandleRequestAsync(request, requestContext, default);

        // Assert
        Assert.Null(commandOrCodeActionContainer);
    }

    [Fact]
    public async Task Handle_NoProviders()
    {
        // Arrange
        var documentPath = new Uri("C:/path/to/Page.razor");
        var codeDocument = CreateCodeDocument("@code {}");
        var documentContext = CreateDocumentContext(documentPath, codeDocument);
        var codeActionEndpoint = new CodeActionEndpoint(
            _documentMappingService,
            Array.Empty<IRazorCodeActionProvider>(),
            Array.Empty<ICSharpCodeActionProvider>(),
            Array.Empty<IHtmlCodeActionProvider>(),
            _clientConnection,
            _languageServerFeatureOptions,
            LoggerFactory,
            telemetryReporter: null)
        {
            _supportsCodeActionResolve = false
        };
        var request = new VSCodeActionParams()
        {
            TextDocument = new VSTextDocumentIdentifier { Uri = documentPath },
            Range = VsLspFactory.CreateZeroWidthRange(0, 1),
            Context = new VSInternalCodeActionContext()
        };
        var requestContext = CreateRazorRequestContext(documentContext);

        // Act
        var commandOrCodeActionContainer = await codeActionEndpoint.HandleRequestAsync(request, requestContext, default);

        // Assert
        Assert.Empty(commandOrCodeActionContainer!);
    }

    [Fact]
    public async Task Handle_OneRazorCodeActionProvider()
    {
        // Arrange
        var documentPath = new Uri("C:/path/to/Page.razor");
        var codeDocument = CreateCodeDocument("@code {}");
        var documentContext = CreateDocumentContext(documentPath, codeDocument);
        var codeActionEndpoint = new CodeActionEndpoint(
            _documentMappingService,
            new IRazorCodeActionProvider[] {
                    new MockRazorCodeActionProvider()
            },
            Array.Empty<ICSharpCodeActionProvider>(),
            Array.Empty<IHtmlCodeActionProvider>(),
            _clientConnection,
            _languageServerFeatureOptions,
            LoggerFactory,
            telemetryReporter: null)
        {
            _supportsCodeActionResolve = false
        };

        var request = new VSCodeActionParams()
        {
            TextDocument = new VSTextDocumentIdentifier { Uri = documentPath },
            Range = VsLspFactory.CreateZeroWidthRange(0, 1),
            Context = new VSInternalCodeActionContext()
        };
        var requestContext = CreateRazorRequestContext(documentContext);

        // Act
        var commandOrCodeActionContainer = await codeActionEndpoint.HandleRequestAsync(request, requestContext, default);

        // Assert
        Assert.NotNull(commandOrCodeActionContainer);
        Assert.Single(commandOrCodeActionContainer);
    }

    [Fact]
    public async Task Handle_OneCSharpCodeActionProvider()
    {
        // Arrange
        var documentPath = new Uri("C:/path/to/Page.razor");
        var codeDocument = CreateCodeDocument("@code {}");
        var documentContext = CreateDocumentContext(documentPath, codeDocument);
        var documentMappingService = CreateDocumentMappingService();
        var languageServer = CreateLanguageServer();
        var codeActionEndpoint = new CodeActionEndpoint(
            documentMappingService,
            Array.Empty<IRazorCodeActionProvider>(),
            new ICSharpCodeActionProvider[] {
                    new MockCSharpCodeActionProvider()
            },
            Array.Empty<IHtmlCodeActionProvider>(),
            languageServer,
            _languageServerFeatureOptions,
            LoggerFactory,
            telemetryReporter: null)
        {
            _supportsCodeActionResolve = false
        };

        var request = new VSCodeActionParams()
        {
            TextDocument = new VSTextDocumentIdentifier { Uri = documentPath },
            Range = VsLspFactory.CreateZeroWidthRange(0, 1),
            Context = new VSInternalCodeActionContext()
        };
        var requestContext = CreateRazorRequestContext(documentContext);

        // Act
        var commandOrCodeActionContainer = await codeActionEndpoint.HandleRequestAsync(request, requestContext, default);

        // Assert
        Assert.NotNull(commandOrCodeActionContainer);
        Assert.Single(commandOrCodeActionContainer);
    }

    [Fact]
    public async Task Handle_OneCodeActionProviderWithMultipleCodeActions()
    {
        // Arrange
        var documentPath = new Uri("C:/path/to/Page.razor");
        var codeDocument = CreateCodeDocument("@code {}");
        var documentContext = CreateDocumentContext(documentPath, codeDocument);
        var codeActionEndpoint = new CodeActionEndpoint(
            _documentMappingService,
            new IRazorCodeActionProvider[] {
                    new MockMultipleRazorCodeActionProvider(),
            },
            Array.Empty<ICSharpCodeActionProvider>(),
            Array.Empty<IHtmlCodeActionProvider>(),
            _clientConnection,
            _languageServerFeatureOptions,
            LoggerFactory,
            telemetryReporter: null)
        {
            _supportsCodeActionResolve = false
        };

        var request = new VSCodeActionParams()
        {
            TextDocument = new VSTextDocumentIdentifier { Uri = documentPath },
            Range = VsLspFactory.CreateZeroWidthRange(0, 1),
            Context = new VSInternalCodeActionContext()
        };
        var requestContext = CreateRazorRequestContext(documentContext);

        // Act
        var commandOrCodeActionContainer = await codeActionEndpoint.HandleRequestAsync(request, requestContext, default);

        // Assert
        Assert.NotNull(commandOrCodeActionContainer);
        Assert.Equal(2, commandOrCodeActionContainer.Length);
    }

    [Fact]
    public async Task Handle_MultipleCodeActionProvidersWithMultipleCodeActions()
    {
        // Arrange
        var documentPath = new Uri("C:/path/to/Page.razor");
        var codeDocument = CreateCodeDocument("@code {}");
        var documentContext = CreateDocumentContext(documentPath, codeDocument);
        var documentMappingService = CreateDocumentMappingService();
        var languageServer = CreateLanguageServer();
        var codeActionEndpoint = new CodeActionEndpoint(
            documentMappingService,
            new IRazorCodeActionProvider[] {
                    new MockMultipleRazorCodeActionProvider(),
                    new MockMultipleRazorCodeActionProvider(),
                    new MockRazorCodeActionProvider(),
            },
            new ICSharpCodeActionProvider[] {
                    new MockCSharpCodeActionProvider(),
                    new MockCSharpCodeActionProvider()
            },
            Array.Empty<IHtmlCodeActionProvider>(),
            languageServer,
            _languageServerFeatureOptions,
            LoggerFactory,
            telemetryReporter: null)
        {
            _supportsCodeActionResolve = false
        };

        var request = new VSCodeActionParams()
        {
            TextDocument = new VSTextDocumentIdentifier { Uri = documentPath },
            Range = VsLspFactory.CreateZeroWidthRange(0, 1),
            Context = new VSInternalCodeActionContext()
        };
        var requestContext = CreateRazorRequestContext(documentContext);

        // Act
        var commandOrCodeActionContainer = await codeActionEndpoint.HandleRequestAsync(request, requestContext, default);

        // Assert
        Assert.NotNull(commandOrCodeActionContainer);
        Assert.Equal(7, commandOrCodeActionContainer.Length);
    }

    [Fact]
    public async Task Handle_MultipleProviders()
    {
        // Arrange
        var documentPath = new Uri("C:/path/to/Page.razor");
        var codeDocument = CreateCodeDocument("@code {}");
        var documentContext = CreateDocumentContext(documentPath, codeDocument);
        var documentMappingService = CreateDocumentMappingService();
        var languageServer = CreateLanguageServer();
        var codeActionEndpoint = new CodeActionEndpoint(
            documentMappingService,
            new IRazorCodeActionProvider[] {
                    new MockRazorCodeActionProvider(),
                    new MockRazorCodeActionProvider(),
                    new MockRazorCodeActionProvider(),
            },
            new ICSharpCodeActionProvider[] {
                    new MockCSharpCodeActionProvider(),
                    new MockCSharpCodeActionProvider()
            },
            Array.Empty<IHtmlCodeActionProvider>(),
            languageServer,
            _languageServerFeatureOptions,
            LoggerFactory,
            telemetryReporter: null)
        {
            _supportsCodeActionResolve = false
        };

        var request = new VSCodeActionParams()
        {
            TextDocument = new VSTextDocumentIdentifier { Uri = documentPath },
            Range = VsLspFactory.CreateZeroWidthRange(0, 1),
            Context = new VSInternalCodeActionContext()
        };
        var requestContext = CreateRazorRequestContext(documentContext);

        // Act
        var commandOrCodeActionContainer = await codeActionEndpoint.HandleRequestAsync(request, requestContext, default);

        // Assert
        Assert.NotNull(commandOrCodeActionContainer);
        Assert.Equal(5, commandOrCodeActionContainer.Length);
    }

    [Fact]
    public async Task Handle_OneNullReturningProvider()
    {
        // Arrange
        var documentPath = new Uri("C:/path/to/Page.razor");
        var codeDocument = CreateCodeDocument("@code {}");
        var documentContext = CreateDocumentContext(documentPath, codeDocument);
        var codeActionEndpoint = new CodeActionEndpoint(
            _documentMappingService,
            new IRazorCodeActionProvider[] {
                    new MockEmptyRazorCodeActionProvider()
            },
            Array.Empty<ICSharpCodeActionProvider>(),
            Array.Empty<IHtmlCodeActionProvider>(),
            _clientConnection,
            _languageServerFeatureOptions,
            LoggerFactory,
            telemetryReporter: null)
        {
            _supportsCodeActionResolve = false
        };

        var request = new VSCodeActionParams()
        {
            TextDocument = new VSTextDocumentIdentifier { Uri = documentPath },
            Range = VsLspFactory.CreateZeroWidthRange(0, 1),
            Context = new VSInternalCodeActionContext()
        };
        var requestContext = CreateRazorRequestContext(documentContext);

        // Act
        var commandOrCodeActionContainer = await codeActionEndpoint.HandleRequestAsync(request, requestContext, default);

        // Assert
        Assert.Empty(commandOrCodeActionContainer!);
    }

    [Fact]
    public async Task Handle_MultipleMixedProvider()
    {
        // Arrange
        var documentPath = new Uri("C:/path/to/Page.razor");
        var codeDocument = CreateCodeDocument("@code {}");
        var documentContext = CreateDocumentContext(documentPath, codeDocument);
        var documentMappingService = CreateDocumentMappingService();
        var languageServer = CreateLanguageServer();
        var codeActionEndpoint = new CodeActionEndpoint(
            documentMappingService,
            new IRazorCodeActionProvider[] {
                    new MockRazorCodeActionProvider(),
                    new MockEmptyRazorCodeActionProvider(),
                    new MockRazorCodeActionProvider(),
                    new MockEmptyRazorCodeActionProvider(),
            },
            new ICSharpCodeActionProvider[] {
                    new MockCSharpCodeActionProvider(),
                    new MockCSharpCodeActionProvider()
            },
            Array.Empty<IHtmlCodeActionProvider>(),
            languageServer,
            _languageServerFeatureOptions,
            LoggerFactory,
            telemetryReporter: null)
        {
            _supportsCodeActionResolve = false
        };

        var request = new VSCodeActionParams()
        {
            TextDocument = new VSTextDocumentIdentifier { Uri = documentPath },
            Range = VsLspFactory.CreateZeroWidthRange(0, 1),
            Context = new VSInternalCodeActionContext()
        };
        var requestContext = CreateRazorRequestContext(documentContext);

        // Act
        var commandOrCodeActionContainer = await codeActionEndpoint.HandleRequestAsync(request, requestContext, default);

        // Assert
        Assert.NotNull(commandOrCodeActionContainer);
        Assert.Equal(4, commandOrCodeActionContainer.Length);
    }

    [Fact]
    public async Task Handle_MultipleMixedProvider_SupportsCodeActionResolveTrue()
    {
        // Arrange
        var documentPath = new Uri("C:/path/to/Page.razor");
        var codeDocument = CreateCodeDocument("@code {}");
        var documentContext = CreateDocumentContext(documentPath, codeDocument);
        var codeActionEndpoint = new CodeActionEndpoint(
            _documentMappingService,
            new IRazorCodeActionProvider[] {
                    new MockRazorCodeActionProvider(),
                    new MockRazorCommandProvider(),
                    new MockEmptyRazorCodeActionProvider()
            },
            Array.Empty<ICSharpCodeActionProvider>(),
            Array.Empty<IHtmlCodeActionProvider>(),
            _clientConnection,
            _languageServerFeatureOptions,
            LoggerFactory,
            telemetryReporter: null)
        {
            _supportsCodeActionResolve = true
        };

        var request = new VSCodeActionParams()
        {
            TextDocument = new VSTextDocumentIdentifier { Uri = documentPath },
            Range = VsLspFactory.CreateZeroWidthRange(0, 1),
            Context = new VSInternalCodeActionContext()
        };
        var requestContext = CreateRazorRequestContext(documentContext);

        // Act
        var commandOrCodeActionContainer = await codeActionEndpoint.HandleRequestAsync(request, requestContext, default);

        // Assert
        Assert.NotNull(commandOrCodeActionContainer);
        Assert.Collection(commandOrCodeActionContainer,
            c =>
            {
                Assert.True(c.TryGetSecond(out var codeAction));
                Assert.True(codeAction is VSInternalCodeAction);
            },
            c =>
            {
                Assert.True(c.TryGetSecond(out var codeAction));
                Assert.True(codeAction is VSInternalCodeAction);
            });
    }

    [Fact]
    public async Task Handle_MixedProvider_SupportsCodeActionResolveTrue_UsesGroups()
    {
        // Arrange
        var documentPath = new Uri("C:/path/to/Page.razor");
        var codeDocument = CreateCodeDocument("@code {}");
        var documentContext = CreateDocumentContext(documentPath, codeDocument);
        var documentMappingService = CreateDocumentMappingService();
        var languageServer = CreateLanguageServer();
        var codeActionEndpoint = new CodeActionEndpoint(
            documentMappingService,
            new IRazorCodeActionProvider[] {
                    new MockRazorCodeActionProvider(),
            },
            new ICSharpCodeActionProvider[] {
                    new MockCSharpCodeActionProvider()
            },
            Array.Empty<IHtmlCodeActionProvider>(),
            languageServer,
            _languageServerFeatureOptions,
            LoggerFactory,
            telemetryReporter: null)
        {
            _supportsCodeActionResolve = true
        };

        var request = new VSCodeActionParams()
        {
            TextDocument = new VSTextDocumentIdentifier { Uri = documentPath },
            Range = VsLspFactory.CreateZeroWidthRange(0, 1),
            Context = new VSInternalCodeActionContext()
        };
        var requestContext = CreateRazorRequestContext(documentContext);

        // Act
        var commandOrCodeActionContainer = await codeActionEndpoint.HandleRequestAsync(request, requestContext, default);

        // Assert
        Assert.NotNull(commandOrCodeActionContainer);
        Assert.Collection(commandOrCodeActionContainer,
            c =>
            {
                Assert.True(c.TryGetSecond(out var codeAction));
                Assert.True(codeAction is VSInternalCodeAction);
                Assert.Equal("A-Razor", ((VSInternalCodeAction)codeAction).Group);
            },
            c =>
            {
                Assert.True(c.TryGetSecond(out var codeAction));
                Assert.True(codeAction is VSInternalCodeAction);
                Assert.Equal("B-Delegated", ((VSInternalCodeAction)codeAction).Group);
            });
    }

    [Fact]
    public async Task Handle_MultipleMixedProvider_SupportsCodeActionResolveFalse()
    {
        // Arrange
        var documentPath = new Uri("C:/path/to/Page.razor");
        var codeDocument = CreateCodeDocument("@code {}");
        var documentContext = CreateDocumentContext(documentPath, codeDocument);
        var codeActionEndpoint = new CodeActionEndpoint(
            _documentMappingService,
            new IRazorCodeActionProvider[] {
                    new MockRazorCodeActionProvider(),
                    new MockRazorCommandProvider(),
                    new MockEmptyRazorCodeActionProvider()
            },
            Array.Empty<ICSharpCodeActionProvider>(),
            Array.Empty<IHtmlCodeActionProvider>(),
            _clientConnection,
            _languageServerFeatureOptions,
            LoggerFactory,
            telemetryReporter: null)
        {
            _supportsCodeActionResolve = false
        };

        var request = new VSCodeActionParams()
        {
            TextDocument = new VSTextDocumentIdentifier { Uri = documentPath },
            Range = VsLspFactory.CreateZeroWidthRange(0, 1),
            Context = new VSInternalCodeActionContext()
        };
        var requestContext = CreateRazorRequestContext(documentContext);

        // Act
        var commandOrCodeActionContainer = await codeActionEndpoint.HandleRequestAsync(request, requestContext, default);

        // Assert
        Assert.NotNull(commandOrCodeActionContainer);
        Assert.Collection(commandOrCodeActionContainer,
            c =>
            {
                Assert.True(c.TryGetFirst(out var command1));
                var command = Assert.IsType<Command>(command1);
                var codeActionParamsToken = (JsonObject)command.Arguments!.First();
                var codeActionParams = codeActionParamsToken.Deserialize<RazorCodeActionResolutionParams>();
                Assert.NotNull(codeActionParams);
                Assert.Equal(LanguageServerConstants.CodeActions.EditBasedCodeActionCommand, codeActionParams.Action);
            },
            c => Assert.True(c.TryGetFirst(out var _)));
    }

    [Fact]
    public async Task GenerateRazorCodeActionContextAsync_WithSelectionRange()
    {
        // Arrange
        var documentPath = new Uri("C:/path/to/Page.razor");
        var codeDocument = CreateCodeDocument("@code {}");
        var documentContext = CreateDocumentContext(documentPath, codeDocument);
        var codeActionEndpoint = new CodeActionEndpoint(
            _documentMappingService,
            new IRazorCodeActionProvider[] {
                    new MockRazorCodeActionProvider()
            },
            Array.Empty<ICSharpCodeActionProvider>(),
            Array.Empty<IHtmlCodeActionProvider>(),
            _clientConnection,
            _languageServerFeatureOptions,
            LoggerFactory,
            telemetryReporter: null)
        {
            _supportsCodeActionResolve = false
        };

        var initialRange = VsLspFactory.CreateZeroWidthRange(0, 1);
        var selectionRange = VsLspFactory.CreateZeroWidthRange(0, 5);
        var request = new VSCodeActionParams()
        {
            TextDocument = new VSTextDocumentIdentifier { Uri = documentPath },
            Range = initialRange,
            Context = new VSInternalCodeActionContext()
            {
                SelectionRange = selectionRange,
            }
        };

        // Act
        var razorCodeActionContext = await codeActionEndpoint.GenerateRazorCodeActionContextAsync(request, documentContext.Snapshot);

        // Assert
        Assert.NotNull(razorCodeActionContext);
        Assert.Equal(selectionRange, razorCodeActionContext.Request.Range);
    }

    [Fact]
    public async Task GenerateRazorCodeActionContextAsync_WithoutSelectionRange()
    {
        // Arrange
        var documentPath = new Uri("C:/path/to/Page.razor");
        var codeDocument = CreateCodeDocument("@code {}");
        var documentContext = CreateDocumentContext(documentPath, codeDocument);
        var codeActionEndpoint = new CodeActionEndpoint(
            _documentMappingService,
            new IRazorCodeActionProvider[] {
                    new MockRazorCodeActionProvider()
            },
            Array.Empty<ICSharpCodeActionProvider>(),
            Array.Empty<IHtmlCodeActionProvider>(),
            _clientConnection,
            _languageServerFeatureOptions,
            LoggerFactory,
            telemetryReporter: null)
        {
            _supportsCodeActionResolve = false
        };

        var initialRange = VsLspFactory.CreateZeroWidthRange(0, 1);
        var request = new VSCodeActionParams()
        {
            TextDocument = new VSTextDocumentIdentifier { Uri = documentPath },
            Range = initialRange,
            Context = new VSInternalCodeActionContext()
            {
                SelectionRange = null
            }
        };

        // Act
        var razorCodeActionContext = await codeActionEndpoint.GenerateRazorCodeActionContextAsync(request, documentContext.Snapshot);

        // Assert
        Assert.NotNull(razorCodeActionContext);
        Assert.Equal(initialRange, razorCodeActionContext.Request.Range);
    }

    [Fact]
    public async Task GetCSharpCodeActionsFromLanguageServerAsync_InvalidRangeMapping()
    {
        // Arrange
        var documentPath = new Uri("C:/path/to/Page.razor");
        var codeDocument = CreateCodeDocument("@code {}");
        var documentContext = CreateDocumentContext(documentPath, codeDocument);
        LinePositionSpan projectedRange = default;
        var documentMappingService = Mock.Of<IDocumentMappingService>(
            d => d.TryMapToGeneratedDocumentRange(It.IsAny<IRazorGeneratedDocument>(), It.IsAny<LinePositionSpan>(), out projectedRange) == false
        , MockBehavior.Strict);
        var codeActionEndpoint = new CodeActionEndpoint(
            documentMappingService,
            Array.Empty<IRazorCodeActionProvider>(),
            new ICSharpCodeActionProvider[] {
                    new MockCSharpCodeActionProvider()
            },
            Array.Empty<IHtmlCodeActionProvider>(),
            _clientConnection,
            _languageServerFeatureOptions,
            LoggerFactory,
            telemetryReporter: null)
        {
            _supportsCodeActionResolve = false
        };

        var initialRange = VsLspFactory.CreateZeroWidthRange(0, 1);
        var request = new VSCodeActionParams()
        {
            TextDocument = new VSTextDocumentIdentifier { Uri = documentPath },
            Range = initialRange,
            Context = new VSInternalCodeActionContext()
        };

        var context = await codeActionEndpoint.GenerateRazorCodeActionContextAsync(request, documentContext.Snapshot);
        Assert.NotNull(context);

        // Act
        var results = await codeActionEndpoint.GetCodeActionsFromLanguageServerAsync(RazorLanguageKind.CSharp, documentContext, context, Guid.Empty, cancellationToken: default);

        // Assert
        Assert.Empty(results);
        Assert.Equal(initialRange, context.Request.Range);
    }

    [Fact]
    public async Task GetCSharpCodeActionsFromLanguageServerAsync_ReturnsCodeActions()
    {
        // Arrange
        var documentPath = new Uri("C:/path/to/Page.razor");
        var codeDocument = CreateCodeDocument("@code {}");
        var documentContext = CreateDocumentContext(documentPath, codeDocument);
        var projectedRange = VsLspFactory.CreateZeroWidthRange(15, 2);
        var documentMappingService = CreateDocumentMappingService(projectedRange.ToLinePositionSpan());
        var languageServer = CreateLanguageServer();
        var codeActionEndpoint = new CodeActionEndpoint(
            documentMappingService,
            Array.Empty<IRazorCodeActionProvider>(),
            new ICSharpCodeActionProvider[] {
                    new MockCSharpCodeActionProvider()
            },
            Array.Empty<IHtmlCodeActionProvider>(),
            languageServer,
            _languageServerFeatureOptions,
            LoggerFactory,
            telemetryReporter: null)
        {
            _supportsCodeActionResolve = false
        };

        var initialRange = VsLspFactory.CreateZeroWidthRange(0, 1);
        var request = new VSCodeActionParams()
        {
            TextDocument = new VSTextDocumentIdentifier { Uri = documentPath },
            Range = initialRange,
            Context = new VSInternalCodeActionContext()
            {
                SelectionRange = initialRange
            }
        };

        var context = await codeActionEndpoint.GenerateRazorCodeActionContextAsync(request, documentContext.Snapshot);
        Assert.NotNull(context);

        // Act
        var results = await codeActionEndpoint.GetCodeActionsFromLanguageServerAsync(RazorLanguageKind.CSharp, documentContext, context, Guid.Empty, cancellationToken: default);

        // Assert
        var result = Assert.Single(results);
        var diagnostics = result.Diagnostics!.ToArray();
        Assert.Equal(2, diagnostics.Length);

        // Diagnostic ranges contain the projected range for
        // 1. Range
        // 2. SelectionRange
        //
        // This helps verify that the CodeActionEndpoint is mapping the
        // ranges correctly using the mapping service
        Assert.Equal(projectedRange, diagnostics[0].Range);
        Assert.Equal(projectedRange, diagnostics[1].Range);
    }

    private static IDocumentMappingService CreateDocumentMappingService(LinePositionSpan projectedRange = default)
    {
        if (projectedRange == default)
        {
            projectedRange = new LinePositionSpan(new(5, 2), new(5, 2));
        }

        var documentMappingService = Mock.Of<IDocumentMappingService>(
            d => d.TryMapToGeneratedDocumentRange(It.IsAny<IRazorGeneratedDocument>(), It.IsAny<LinePositionSpan>(), out projectedRange) == true &&
                 d.GetLanguageKind(It.IsAny<RazorCodeDocument>(), It.IsAny<int>(), It.IsAny<bool>()) == RazorLanguageKind.CSharp
        , MockBehavior.Strict);
        return documentMappingService;
    }

    private static IClientConnection CreateLanguageServer()
    {
        return new TestLanguageServer();
    }

    private static RazorCodeDocument CreateCodeDocument(string text)
    {
        var codeDocument = TestRazorCodeDocument.Create(text);
        var sourceDocument = TestRazorSourceDocument.Create(text);
        var syntaxTree = RazorSyntaxTree.Parse(sourceDocument);
        codeDocument.SetSyntaxTree(syntaxTree);
        return codeDocument;
    }

    private class MockRazorCodeActionProvider : IRazorCodeActionProvider
    {
        public Task<ImmutableArray<RazorVSInternalCodeAction>> ProvideAsync(RazorCodeActionContext context, CancellationToken cancellationToken)
        {
            return Task.FromResult<ImmutableArray<RazorVSInternalCodeAction>>([new RazorVSInternalCodeAction()]);
        }
    }

    private class MockMultipleRazorCodeActionProvider : IRazorCodeActionProvider
    {
        public Task<ImmutableArray<RazorVSInternalCodeAction>> ProvideAsync(RazorCodeActionContext context, CancellationToken cancellationToken)
        {
            return Task.FromResult<ImmutableArray<RazorVSInternalCodeAction>>(
                [
                    new RazorVSInternalCodeAction(),
                    new RazorVSInternalCodeAction()
                ]);
        }
    }

    private class MockCSharpCodeActionProvider : ICSharpCodeActionProvider
    {
        public Task<ImmutableArray<RazorVSInternalCodeAction>> ProvideAsync(RazorCodeActionContext _1, ImmutableArray<RazorVSInternalCodeAction> _2, CancellationToken _3)
        {
            return Task.FromResult<ImmutableArray<RazorVSInternalCodeAction>>(
                [
                    new RazorVSInternalCodeAction()
                ]);
        }
    }

    private class MockRazorCommandProvider : IRazorCodeActionProvider
    {
        public Task<ImmutableArray<RazorVSInternalCodeAction>> ProvideAsync(RazorCodeActionContext _1, CancellationToken _2)
        {
            // O# Code Actions don't have `Data`, but `Commands` do
            return Task.FromResult<ImmutableArray<RazorVSInternalCodeAction>>(
                [
                    new RazorVSInternalCodeAction() {
                        Title = "SomeTitle",
                        Data = JsonSerializer.SerializeToElement(new AddUsingsCodeActionParams()
                        {
                            Namespace="Test",
                            Uri = new Uri("C:/path/to/Page.razor")
                        })
                    }
                ]);
        }
    }

    private class MockEmptyRazorCodeActionProvider : IRazorCodeActionProvider
    {
        public Task<ImmutableArray<RazorVSInternalCodeAction>> ProvideAsync(RazorCodeActionContext _1, CancellationToken _2)
        {
            return SpecializedTasks.EmptyImmutableArray<RazorVSInternalCodeAction>();
        }
    }

    private class TestLanguageServer : IClientConnection
    {
        public Task SendNotificationAsync<TParams>(string method, TParams @params, CancellationToken cancellationToken)
        {
            if (method != CustomMessageNames.RazorProvideCodeActionsEndpoint)
            {
                throw new InvalidOperationException($"Unexpected method {method}");
            }

            return Task.CompletedTask;
        }

        public Task SendNotificationAsync(string method, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public Task<TResponse> SendRequestAsync<TParams, TResponse>(string method, TParams @params, CancellationToken cancellationToken)
        {
            if (method != CustomMessageNames.RazorProvideCodeActionsEndpoint)
            {
                throw new InvalidOperationException($"Unexpected method {method}");
            }

            if (@params is not DelegatedCodeActionParams delegatedCodeActionParams ||
                delegatedCodeActionParams.CodeActionParams is not VSCodeActionParams codeActionParams ||
                codeActionParams.Context is not VSInternalCodeActionContext codeActionContext)
            {
                throw new InvalidOperationException(@params!.GetType().FullName);
            }

            var diagnostics = new List<Diagnostic>
            {
                new Diagnostic()
                {
                    Range = codeActionParams.Range,
                    Message = "Range"
                }
            };
            if (codeActionContext.SelectionRange is not null)
            {
                diagnostics.Add(new Diagnostic()
                {
                    Range = codeActionContext.SelectionRange,
                    Message = "Selection Range"
                });
            }

            // Create a code action specifically with diagnostics that
            // contain the contextual information for it's creation. This is
            // a hacky way to verify that data transmitted to the language server
            // is correct rather than providing specific test hooks in the CodeActionEndpoint
            var result = new[]
            {
                    new RazorVSInternalCodeAction()
                    {
                        Data = JsonSerializer.SerializeToElement(new { CustomTags = new object[] { "CodeActionName" } }),
                        Diagnostics = diagnostics.ToArray()
                    }
                };

            return Task.FromResult((TResponse)(object)result);
        }
    }
}
