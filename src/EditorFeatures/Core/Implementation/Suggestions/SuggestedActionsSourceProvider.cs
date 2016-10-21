// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CodeFixes.Suppression;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Editor.Host;
using Microsoft.CodeAnalysis.Editor.Shared;
using Microsoft.CodeAnalysis.Editor.Shared.Extensions;
using Microsoft.CodeAnalysis.Editor.Shared.Options;
using Microsoft.CodeAnalysis.Editor.Shared.Utilities;
using Microsoft.CodeAnalysis.Experiments;
using Microsoft.CodeAnalysis.Internal.Log;
using Microsoft.CodeAnalysis.Shared.TestHooks;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.Text.Shared.Extensions;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.Editor.Implementation.Suggestions
{
    using CodeFixGroupKey = Tuple<DiagnosticData, CodeActionPriority>;

    [Export(typeof(ISuggestedActionsSourceProvider))]
    [Export(typeof(SuggestedActionsSourceProvider))]
    [VisualStudio.Utilities.ContentType(ContentTypeNames.RoslynContentType)]
    [VisualStudio.Utilities.ContentType(ContentTypeNames.XamlContentType)]
    [VisualStudio.Utilities.Name("Roslyn Code Fix")]
    [VisualStudio.Utilities.Order]
    internal class SuggestedActionsSourceProvider : ISuggestedActionsSourceProvider
    {
        private static readonly Guid s_CSharpSourceGuid = new Guid("b967fea8-e2c3-4984-87d4-71a38f49e16a");
        private static readonly Guid s_visualBasicSourceGuid = new Guid("4de30e93-3e0c-40c2-a4ba-1124da4539f6");
        private static readonly Guid s_xamlSourceGuid = new Guid("a0572245-2eab-4c39-9f61-06a6d8c5ddda");

        private const int InvalidSolutionVersion = -1;

        private readonly ICodeRefactoringService _codeRefactoringService;
        private readonly IDiagnosticAnalyzerService _diagnosticService;
        private readonly ICodeFixService _codeFixService;

        public readonly ICodeActionEditHandlerService EditHandler;
        public readonly IAsynchronousOperationListener OperationListener;
        public readonly IWaitIndicator WaitIndicator;

        [ImportingConstructor]
        public SuggestedActionsSourceProvider(
            ICodeRefactoringService codeRefactoringService,
            IDiagnosticAnalyzerService diagnosticService,
            ICodeFixService codeFixService,
            ICodeActionEditHandlerService editHandler,
            IWaitIndicator waitIndicator,
            [ImportMany] IEnumerable<Lazy<IAsynchronousOperationListener, FeatureMetadata>> asyncListeners)
        {
            _codeRefactoringService = codeRefactoringService;
            _diagnosticService = diagnosticService;
            _codeFixService = codeFixService;
            EditHandler = editHandler;
            WaitIndicator = waitIndicator;
            OperationListener = new AggregateAsynchronousOperationListener(asyncListeners, FeatureAttribute.LightBulb);
        }

        public ISuggestedActionsSource CreateSuggestedActionsSource(ITextView textView, ITextBuffer textBuffer)
        {
            Contract.ThrowIfNull(textView);
            Contract.ThrowIfNull(textBuffer);

            return new Source(this, textView, textBuffer);
        }

        private class Source : ForegroundThreadAffinitizedObject, ISuggestedActionsSource
        {
            // state that will be only reset when source is disposed.
            private SuggestedActionsSourceProvider _owner;
            private ITextView _textView;
            private ITextBuffer _subjectBuffer;
            private WorkspaceRegistration _registration;

            // mutable state
            private Workspace _workspace;
            private int _lastSolutionVersionReported;

            public Source(SuggestedActionsSourceProvider owner, ITextView textView, ITextBuffer textBuffer)
            {
                _owner = owner;
                _textView = textView;
                _textView.Closed += OnTextViewClosed;

                _subjectBuffer = textBuffer;
                _registration = Workspace.GetWorkspaceRegistration(textBuffer.AsTextContainer());

                _lastSolutionVersionReported = InvalidSolutionVersion;
                var updateSource = (IDiagnosticUpdateSource)_owner._diagnosticService;
                updateSource.DiagnosticsUpdated += OnDiagnosticsUpdated;

                if (_registration.Workspace != null)
                {
                    _workspace = _registration.Workspace;
                    _workspace.DocumentActiveContextChanged += OnActiveContextChanged;
                }

                _registration.WorkspaceChanged += OnWorkspaceChanged;
            }

            public event EventHandler<EventArgs> SuggestedActionsChanged;

            public bool TryGetTelemetryId(out Guid telemetryId)
            {
                telemetryId = default(Guid);

                var workspace = _workspace;
                if (workspace == null || _subjectBuffer == null)
                {
                    return false;
                }

                var documentId = workspace.GetDocumentIdInCurrentContext(_subjectBuffer.AsTextContainer());
                if (documentId == null)
                {
                    return false;
                }

                var project = workspace.CurrentSolution.GetProject(documentId.ProjectId);
                if (project == null)
                {
                    return false;
                }

                switch (project.Language)
                {
                    case LanguageNames.CSharp:
                        telemetryId = s_CSharpSourceGuid;
                        return true;
                    case LanguageNames.VisualBasic:
                        telemetryId = s_visualBasicSourceGuid;
                        return true;
                    case "Xaml":
                        telemetryId = s_xamlSourceGuid;
                        return true;
                    default:
                        return false;
                }
            }

            public IEnumerable<SuggestedActionSet> GetSuggestedActions(ISuggestedActionCategorySet requestedActionCategories, SnapshotSpan range, CancellationToken cancellationToken)
            {
                AssertIsForeground();

                using (Logger.LogBlock(FunctionId.SuggestedActions_GetSuggestedActions, cancellationToken))
                {
                    var documentAndSnapshot = GetMatchingDocumentAndSnapshotAsync(range.Snapshot, cancellationToken).WaitAndGetResult(cancellationToken);
                    if (!documentAndSnapshot.HasValue)
                    {
                        // this is here to fail test and see why it is failed.
                        Trace.WriteLine("given range is not current");
                        return null;
                    }

                    var document = documentAndSnapshot.Value.Item1;
                    var workspace = document.Project.Solution.Workspace;
                    var supportsFeatureService = workspace.Services.GetService<IDocumentSupportsFeatureService>();

                    var fixes = GetCodeFixes(supportsFeatureService, requestedActionCategories, workspace, document, range, cancellationToken);
                    var refactorings = GetRefactorings(supportsFeatureService, requestedActionCategories, workspace, document, range, cancellationToken);

                    var result = fixes == null ? refactorings : refactorings == null
                                               ? fixes : fixes.Concat(refactorings);

                    if (result == null)
                    {
                        return null;
                    }

                    var allActionSets = result.ToList();
                    allActionSets = InlineActionSetsIfDesirable(allActionSets);
                    return allActionSets;
                }
            }

            private List<SuggestedActionSet> InlineActionSetsIfDesirable(List<SuggestedActionSet> allActionSets)
            {
                // If we only have a single set of items, and that set only has three max suggestion 
                // offered.  Then we can consider inlining any nested actions into the top level list.
                // (but we only do this if the parent of the nested actions isn't invokable itself).
                if (allActionSets.Sum(a => a.Actions.Count()) > 3)
                {
                    return allActionSets;
                }

                return allActionSets.Select(InlineActions).ToList();
            }

            private SuggestedActionSet InlineActions(SuggestedActionSet actionSet)
            {
                var newActions = ArrayBuilder<ISuggestedAction>.GetInstance();
                foreach (var action in actionSet.Actions)
                {
                    var actionWithNestedActions = action as SuggestedActionWithNestedActions;

                    // Only inline if the underlying code action allows it.
                    if (actionWithNestedActions?.CodeAction.IsInlinable == true)
                    {
                        newActions.AddRange(actionWithNestedActions.NestedActionSet.Actions);
                    }
                    else
                    {
                        newActions.Add(action);
                    }
                }

                return new SuggestedActionSet(
                    newActions.ToImmutableAndFree(), actionSet.Title, actionSet.Priority, actionSet.ApplicableToSpan);
            }

            private IEnumerable<SuggestedActionSet> GetCodeFixes(
                IDocumentSupportsFeatureService supportsFeatureService,
                ISuggestedActionCategorySet requestedActionCategories,
                Workspace workspace,
                Document document,
                SnapshotSpan range,
                CancellationToken cancellationToken)
            {
                this.AssertIsForeground();

                if (_owner._codeFixService != null &&
                    supportsFeatureService.SupportsCodeFixes(document) &&
                    requestedActionCategories.Contains(PredefinedSuggestedActionCategoryNames.CodeFix))
                {
                    // We only include suppressions if lightbulb is asking for everything.
                    // If the light bulb is only asking for code fixes, then we don't include suppressions.
                    var includeSuppressionFixes = requestedActionCategories.Contains(PredefinedSuggestedActionCategoryNames.Any);

                    var fixes = Task.Run(
                        () => _owner._codeFixService.GetFixesAsync(
                                document, range.Span.ToTextSpan(), includeSuppressionFixes, cancellationToken),
                        cancellationToken).WaitAndGetResult(cancellationToken);

                    var filteredFixes = FilterOnUIThread(fixes, workspace);

                    return OrganizeFixes(workspace, filteredFixes, includeSuppressionFixes: includeSuppressionFixes);
                }

                return null;
            }

            private List<CodeFixCollection> FilterOnUIThread(
                ImmutableArray<CodeFixCollection> collections, Workspace workspace)
            {
                this.AssertIsForeground();

                return collections.Select(c => FilterOnUIThread(c, workspace)).WhereNotNull().ToList();
            }

            private CodeFixCollection FilterOnUIThread(
                CodeFixCollection collection,
                Workspace workspace)
            {
                this.AssertIsForeground();

                var applicableFixes = collection.Fixes.Where(f => IsApplicable(f.Action, workspace)).ToList();
                return applicableFixes.Count == 0
                    ? null
                    : applicableFixes.Count == collection.Fixes.Length
                        ? collection
                        : new CodeFixCollection(collection.Provider, collection.TextSpan, applicableFixes, 
                            collection.FixAllState, 
                            collection.SupportedScopes, collection.FirstDiagnostic);
            }

            private bool IsApplicable(CodeAction action, Workspace workspace)
            {
                if (!action.PerformFinalApplicabilityCheck)
                {
                    // If we don't even need to perform the final applicability check,
                    // then the code actoin is applicable.
                    return true;
                }

                // Otherwise, defer to the action to make the decision.
                this.AssertIsForeground();
                return action.IsApplicable(workspace);
            }

            private List<CodeRefactoring> FilterOnUIThread(List<CodeRefactoring> refactorings, Workspace workspace)
            {
                return refactorings.Select(r => FilterOnUIThread(r, workspace)).WhereNotNull().ToList();
            }

            private CodeRefactoring FilterOnUIThread(CodeRefactoring refactoring, Workspace workspace)
            {
                var actions = refactoring.Actions.Where(a => IsApplicable(a, workspace)).ToList();
                return actions.Count == 0
                    ? null
                    : actions.Count == refactoring.Actions.Count
                        ? refactoring
                        : new CodeRefactoring(refactoring.Provider, actions);
            }

            /// <summary>
            /// Arrange fixes into groups based on the issue (diagnostic being fixed) and prioritize these groups.
            /// </summary>
            private IEnumerable<SuggestedActionSet> OrganizeFixes(
                Workspace workspace, IEnumerable<CodeFixCollection> fixCollections,
                bool includeSuppressionFixes)
            {
                var map = ImmutableDictionary.CreateBuilder<CodeFixGroupKey, IList<SuggestedAction>>();
                var order = ArrayBuilder<CodeFixGroupKey>.GetInstance();

                // First group fixes by diagnostic and priority.
                GroupFixes(workspace, fixCollections, map, order, includeSuppressionFixes);

                // Then prioritize between the groups.
                return PrioritizeFixGroups(map.ToImmutable(), order.ToImmutableAndFree());
            }

            /// <summary>
            /// Groups fixes by the diagnostic being addressed by each fix.
            /// </summary>
            private void GroupFixes(
                Workspace workspace,
                IEnumerable<CodeFixCollection> fixCollections,
                IDictionary<CodeFixGroupKey, IList<SuggestedAction>> map,
                ArrayBuilder<CodeFixGroupKey> order,
                bool includeSuppressionFixes)
            {
                foreach (var fixCollection in fixCollections)
                {
                    ProcessFixCollection(
                        workspace, map, order, includeSuppressionFixes, fixCollection);
                }
            }

            private void ProcessFixCollection(Workspace workspace, IDictionary<CodeFixGroupKey, IList<SuggestedAction>> map, ArrayBuilder<CodeFixGroupKey> order, bool includeSuppressionFixes, CodeFixCollection fixCollection)
            {
                var fixes = fixCollection.Fixes;
                var fixCount = fixes.Length;

                Func<CodeAction, SuggestedActionSet> getFixAllSuggestedActionSet =
                    codeAction => GetFixAllSuggestedActionSet(
                        codeAction, fixCount, fixCollection.FixAllState,
                        fixCollection.SupportedScopes, fixCollection.FirstDiagnostic,
                        workspace);

                // Suppression fixes are handled below.
                foreach (var fix in fixes.Where(f => !(f.Action is SuppressionCodeAction)))
                {
                    SuggestedAction suggestedAction;
                    if (fix.Action.HasCodeActions)
                    {
                        var nestedActions = fix.Action.GetNestedCodeActions().SelectAsArray(
                            nestedAction => new CodeFixSuggestedAction(
                                _owner, workspace, _subjectBuffer, fix, fixCollection.Provider,
                                getFixAllSuggestedActionSet(nestedAction), nestedAction));

                        var set = new SuggestedActionSet(
                            nestedActions, SuggestedActionSetPriority.Medium, 
                            fix.PrimaryDiagnostic.Location.SourceSpan.ToSpan());

                        suggestedAction = new SuggestedActionWithNestedActions(
                            _owner, workspace, _subjectBuffer,
                            fixCollection.Provider, fix.Action, set);
                    }
                    else
                    {
                        suggestedAction = new CodeFixSuggestedAction(
                            _owner, workspace, _subjectBuffer,  fix, fixCollection.Provider, 
                            getFixAllSuggestedActionSet(fix.Action), fix.Action);
                    }

                    AddFix(fix, suggestedAction, map, order);
                }

                if (!includeSuppressionFixes)
                {
                    return;
                }

                // Add suppression fixes to the end of a given SuggestedActionSet so that they always show up last in a group.
                foreach (var fix in fixes.Where(f => f.Action is SuppressionCodeAction))
                {
                    SuggestedAction suggestedAction;
                    if (fix.Action.HasCodeActions)
                    {
                        suggestedAction = new SuppressionSuggestedAction(
                            _owner, workspace, _subjectBuffer, fix, fixCollection.Provider, 
                            getFixAllSuggestedActionSet);
                    }
                    else
                    {
                        suggestedAction = new CodeFixSuggestedAction(
                            _owner, workspace, _subjectBuffer, fix, fixCollection.Provider, 
                            getFixAllSuggestedActionSet(fix.Action), fix.Action);
                    }

                    AddFix(fix, suggestedAction, map, order);
                }
            }

            private static void AddFix(
                CodeFix fix, SuggestedAction suggestedAction,
                IDictionary<CodeFixGroupKey, IList<SuggestedAction>> map,
                ArrayBuilder<CodeFixGroupKey> order)
            {
                var diag = fix.GetPrimaryDiagnosticData();

                var groupKey = new CodeFixGroupKey(diag, fix.Action.Priority);
                if (!map.ContainsKey(groupKey))
                {
                    order.Add(groupKey);
                    map[groupKey] = ImmutableArray.CreateBuilder<SuggestedAction>();
                }

                map[groupKey].Add(suggestedAction);
            }

            /// <summary>
            /// If the provided fix all context is non-null and the context's code action Id matches the given code action's Id then,
            /// returns the set of fix all occurrences actions associated with the code action.
            /// </summary>
            internal SuggestedActionSet GetFixAllSuggestedActionSet(
                CodeAction action,
                int actionCount,
                FixAllState fixAllState,
                IEnumerable<FixAllScope> supportedScopes,
                Diagnostic firstDiagnostic,
                Workspace workspace)
            {

                if (fixAllState == null)
                {
                    return null;
                }

                if (actionCount > 1 && action.EquivalenceKey == null)
                {
                    return null;
                }

                var fixAllSuggestedActions = ArrayBuilder<FixAllSuggestedAction>.GetInstance();
                foreach (var scope in supportedScopes)
                {
                    var fixAllStateForScope = fixAllState.WithScopeAndEquivalenceKey(scope, action.EquivalenceKey);

                    var fixAllSuggestedAction = new FixAllSuggestedAction(
                        _owner, workspace, _subjectBuffer, fixAllStateForScope, firstDiagnostic);

                    fixAllSuggestedActions.Add(fixAllSuggestedAction);
                }

                return new SuggestedActionSet(
                    fixAllSuggestedActions.ToImmutableAndFree(),
                    title: EditorFeaturesResources.Fix_all_occurrences_in);
            }

            /// <summary>
            /// Return prioritized set of fix groups such that fix group for suppression always show up at the bottom of the list.
            /// </summary>
            /// <remarks>
            /// Fix groups are returned in priority order determined based on <see cref="ExtensionOrderAttribute"/>.
            /// Priority for all <see cref="SuggestedActionSet"/>s containing fixes is set to <see cref="SuggestedActionSetPriority.Medium"/> by default.
            /// The only exception is the case where a <see cref="SuggestedActionSet"/> only contains suppression fixes -
            /// the priority of such <see cref="SuggestedActionSet"/>s is set to <see cref="SuggestedActionSetPriority.None"/> so that suppression fixes
            /// always show up last after all other fixes (and refactorings) for the selected line of code.
            /// </remarks>
            private static ImmutableArray<SuggestedActionSet> PrioritizeFixGroups(
                IDictionary<CodeFixGroupKey, IList<SuggestedAction>> map, IList<CodeFixGroupKey> order)
            {
                var sets = ArrayBuilder<SuggestedActionSet>.GetInstance();

                foreach (var diag in order)
                {
                    var actions = map[diag];

                    foreach (var group in actions.GroupBy(a => a.Priority))
                    {
                        var priority = GetSuggestedActionSetPriority(group.Key);

                        // diagnostic from things like build shouldn't reach here since we don't support LB for those diagnostics
                        Contract.Requires(diag.Item1.HasTextSpan);
                        sets.Add(new SuggestedActionSet(group, priority, diag.Item1.TextSpan.ToSpan()));
                    }
                }

                return sets.ToImmutableAndFree();
            }

            private static SuggestedActionSetPriority GetSuggestedActionSetPriority(CodeActionPriority key)
            {
                switch (key)
                {
                    case CodeActionPriority.None: return SuggestedActionSetPriority.None;
                    case CodeActionPriority.Low: return SuggestedActionSetPriority.Low;
                    case CodeActionPriority.Medium: return SuggestedActionSetPriority.Medium;
                    case CodeActionPriority.High: return SuggestedActionSetPriority.High;
                    default:
                        throw new InvalidOperationException();
                }
            }

            private IEnumerable<SuggestedActionSet> GetRefactorings(
                IDocumentSupportsFeatureService supportsFeatureService,
                ISuggestedActionCategorySet requestedActionCategories,
                Workspace workspace,
                Document document,
                SnapshotSpan range,
                CancellationToken cancellationToken)
            {
                this.AssertIsForeground();


                if (workspace.Options.GetOption(EditorComponentOnOffOptions.CodeRefactorings) &&
                    _owner._codeRefactoringService != null &&
                    supportsFeatureService.SupportsRefactorings(document) &&
                    requestedActionCategories.Contains(PredefinedSuggestedActionCategoryNames.Refactoring))
                {
                    // Get the selection while on the UI thread.
                    var selection = TryGetCodeRefactoringSelection(_subjectBuffer, _textView, range);
                    if (!selection.HasValue)
                    {
                        // this is here to fail test and see why it is failed.
                        Trace.WriteLine("given range is not current");
                        return null;
                    }

                    var refactorings = Task.Run(
                        async () =>
                        {
                            var stream = await _owner._codeRefactoringService.GetRefactoringsAsync(
                                document, selection.Value, cancellationToken).ConfigureAwait(false);
                            return stream.ToList();
                        },
                        cancellationToken).WaitAndGetResult(cancellationToken);

                    var filteredRefactorings = FilterOnUIThread(refactorings, workspace);

                    return filteredRefactorings.Select(r => OrganizeRefactorings(workspace, r));
                }

                return null;
            }

            /// <summary>
            /// Arrange refactorings into groups.
            /// </summary>
            /// <remarks>
            /// Refactorings are returned in priority order determined based on <see cref="ExtensionOrderAttribute"/>.
            /// Priority for all <see cref="SuggestedActionSet"/>s containing refactorings is set to <see cref="SuggestedActionSetPriority.Low"/>
            /// and should show up after fixes but before suppression fixes in the light bulb menu.
            /// </remarks>
            private SuggestedActionSet OrganizeRefactorings(Workspace workspace, CodeRefactoring refactoring)
            {
                var refactoringSuggestedActions = ArrayBuilder<SuggestedAction>.GetInstance();

                foreach (var action in refactoring.Actions)
                {
                    refactoringSuggestedActions.Add(new CodeRefactoringSuggestedAction(
                        _owner, workspace, _subjectBuffer, refactoring.Provider, action));
                }

                return new SuggestedActionSet(
                    refactoringSuggestedActions.ToImmutableAndFree(), SuggestedActionSetPriority.Low);
            }

            public async Task<bool> HasSuggestedActionsAsync(ISuggestedActionCategorySet requestedActionCategories, SnapshotSpan range, CancellationToken cancellationToken)
            {
                // Explicitly hold onto below fields in locals and use these locals throughout this code path to avoid crashes
                // if these fields happen to be cleared by Dispose() below. This is required since this code path involves
                // code that can run asynchronously from background thread.
                var view = _textView;
                var buffer = _subjectBuffer;
                var provider = _owner;

                if (view == null || buffer == null || provider == null)
                {
                    return false;
                }

                using (var asyncToken = provider.OperationListener.BeginAsyncOperation("HasSuggestedActionsAsync"))
                {
                    var documentAndSnapshot = await GetMatchingDocumentAndSnapshotAsync(range.Snapshot, cancellationToken).ConfigureAwait(false);
                    if (!documentAndSnapshot.HasValue)
                    {
                        // this is here to fail test and see why it is failed.
                        Trace.WriteLine("given range is not current");
                        return false;
                    }

                    var document = documentAndSnapshot.Value.Item1;
                    var workspace = document.Project.Solution.Workspace;
                    var supportsFeatureService = workspace.Services.GetService<IDocumentSupportsFeatureService>();

                    return
                        await HasFixesAsync(
                            supportsFeatureService, requestedActionCategories, provider, document, range,
                            cancellationToken).ConfigureAwait(false) ||
                        await HasRefactoringsAsync(
                            supportsFeatureService, requestedActionCategories, provider, document, buffer, view, range,
                            cancellationToken).ConfigureAwait(false);
                }
            }

            private async Task<bool> HasFixesAsync(
                IDocumentSupportsFeatureService supportsFeatureService,
                ISuggestedActionCategorySet requestedActionCategories,
                SuggestedActionsSourceProvider provider,
                Document document, SnapshotSpan range,
                CancellationToken cancellationToken)
            {
                if (provider._codeFixService != null && supportsFeatureService.SupportsCodeFixes(document) &&
                    requestedActionCategories.Contains(PredefinedSuggestedActionCategoryNames.CodeFix))
                {
                    // We only consider suppressions if lightbulb is asking for everything.
                    // If the light bulb is only asking for code fixes, then we don't consider suppressions.
                    var considerSuppressionFixes = requestedActionCategories.Contains(PredefinedSuggestedActionCategoryNames.Any);
                    var result = await Task.Run(
                        () => provider._codeFixService.GetFirstDiagnosticWithFixAsync(
                            document, range.Span.ToTextSpan(), considerSuppressionFixes, cancellationToken),
                        cancellationToken).ConfigureAwait(false);

                    if (result.HasFix)
                    {
                        Logger.Log(FunctionId.SuggestedActions_HasSuggestedActionsAsync);
                        return true;
                    }

                    if (result.PartialResult)
                    {
                        // reset solution version number so that we can raise suggested action changed event
                        Volatile.Write(ref _lastSolutionVersionReported, InvalidSolutionVersion);
                        return false;
                    }
                }

                return false;
            }

            private async Task<bool> HasRefactoringsAsync(
                IDocumentSupportsFeatureService supportsFeatureService,
                ISuggestedActionCategorySet requestedActionCategories,
                SuggestedActionsSourceProvider provider,
                Document document,
                ITextBuffer buffer,
                ITextView view,
                SnapshotSpan range,
                CancellationToken cancellationToken)
            {
                if (!requestedActionCategories.Contains(PredefinedSuggestedActionCategoryNames.Refactoring))
                {
                    // See if we should still show the light bulb, even if we weren't explicitly 
                    // asked for refactorings.  We'll show the lightbulb if we're currently
                    // flighting the "Refactoring" A/B test, or if a special option is set
                    // enabling this internally.

                    var workspace = document.Project.Solution.Workspace;
                    var experimentationService = workspace.Services.GetService<IExperimentationService>();
                    if (!experimentationService.IsExperimentEnabled("Refactoring") &&
                        !workspace.Options.GetOption(EditorComponentOnOffOptions.ShowCodeRefactoringsWhenQueriedForCodeFixes))
                    {
                        return false;
                    }
                }

                if (document.Project.Solution.Options.GetOption(EditorComponentOnOffOptions.CodeRefactorings) &&
                    provider._codeRefactoringService != null &&
                    supportsFeatureService.SupportsRefactorings(document))
                {
                    TextSpan? selection = null;
                    if (IsForeground())
                    {
                        // This operation needs to happen on UI thread because it needs to access textView.Selection.
                        selection = TryGetCodeRefactoringSelection(buffer, view, range);
                    }
                    else
                    {
                        await InvokeBelowInputPriority(() =>
                        {
                            // This operation needs to happen on UI thread because it needs to access textView.Selection.
                            selection = TryGetCodeRefactoringSelection(buffer, view, range);
                        }).ConfigureAwait(false);
                    }

                    if (!selection.HasValue)
                    {
                        // this is here to fail test and see why it is failed.
                        Trace.WriteLine("given range is not current");
                        return false;
                    }

                    return await Task.Run(
                        () => provider._codeRefactoringService.HasRefactoringsAsync(
                            document, selection.Value, cancellationToken),
                        cancellationToken).ConfigureAwait(false);
                }

                return false;
            }

            private static TextSpan? TryGetCodeRefactoringSelection(ITextBuffer buffer, ITextView view, SnapshotSpan range)
            {
                var selectedSpans = view.Selection.SelectedSpans
                    .SelectMany(ss => view.BufferGraph.MapDownToBuffer(ss, SpanTrackingMode.EdgeExclusive, buffer))
                    .Where(ss => !view.IsReadOnlyOnSurfaceBuffer(ss))
                    .ToList();

                // We only support refactorings when there is a single selection in the document.
                if (selectedSpans.Count != 1)
                {
                    return null;
                }

                var translatedSpan = selectedSpans[0].TranslateTo(range.Snapshot, SpanTrackingMode.EdgeInclusive);

                // We only support refactorings when selected span intersects with the span that the light bulb is asking for.
                if (!translatedSpan.IntersectsWith(range))
                {
                    return null;
                }

                return translatedSpan.Span.ToTextSpan();
            }

            private static async Task<ValueTuple<Document, ITextSnapshot>?> GetMatchingDocumentAndSnapshotAsync(ITextSnapshot givenSnapshot, CancellationToken cancellationToken)
            {
                var buffer = givenSnapshot.TextBuffer;
                if (buffer == null)
                {
                    return null;
                }

                var workspace = buffer.GetWorkspace();
                if (workspace == null)
                {
                    return null;
                }

                var documentId = workspace.GetDocumentIdInCurrentContext(buffer.AsTextContainer());
                if (documentId == null)
                {
                    return null;
                }

                var document = workspace.CurrentSolution.GetDocument(documentId);
                if (document == null)
                {
                    return null;
                }

                var sourceText = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                var snapshot = sourceText.FindCorrespondingEditorTextSnapshot();
                if (snapshot == null || snapshot.Version.ReiteratedVersionNumber != givenSnapshot.Version.ReiteratedVersionNumber)
                {
                    return null;
                }

                return ValueTuple.Create(document, snapshot);
            }

            private void OnTextViewClosed(object sender, EventArgs e)
            {
                Dispose();
            }

            private void OnWorkspaceChanged(object sender, EventArgs e)
            {
                // REVIEW: this event should give both old and new workspace as argument so that
                // one doesn't need to hold onto workspace in field.

                // remove existing event registration
                if (_workspace != null)
                {
                    _workspace.DocumentActiveContextChanged -= OnActiveContextChanged;
                }

                // REVIEW: why one need to get new workspace from registration? why not just pass in the new workspace?
                // add new event registration
                _workspace = _registration.Workspace;

                if (_workspace != null)
                {
                    _workspace.DocumentActiveContextChanged += OnActiveContextChanged;
                }
            }

            private void OnActiveContextChanged(object sender, DocumentActiveContextChangedEventArgs e)
            {
                // REVIEW: it would be nice for changed event to pass in both old and new document.
                OnSuggestedActionsChanged(e.Solution.Workspace, e.NewActiveContextDocumentId, e.Solution.WorkspaceVersion);
            }

            private void OnDiagnosticsUpdated(object sender, DiagnosticsUpdatedArgs e)
            {
                // document removed case. no reason to raise event
                if (e.Solution == null)
                {
                    return;
                }

                OnSuggestedActionsChanged(e.Workspace, e.DocumentId, e.Solution.WorkspaceVersion);
            }

            private void OnSuggestedActionsChanged(Workspace currentWorkspace, DocumentId currentDocumentId, int solutionVersion, DiagnosticsUpdatedArgs args = null)
            {
                // Explicitly hold onto the _subjectBuffer field in a local and use this local in this function to avoid crashes
                // if this field happens to be cleared by Dispose() below. This is required since this code path involves code
                // that can run on background thread.
                var buffer = _subjectBuffer;
                if (buffer == null)
                {
                    return;
                }

                var workspace = buffer.GetWorkspace();

                // workspace is not ready, nothing to do.
                if (workspace == null || workspace != currentWorkspace)
                {
                    return;
                }

                if (currentDocumentId != workspace.GetDocumentIdInCurrentContext(buffer.AsTextContainer()) ||
                    solutionVersion == Volatile.Read(ref _lastSolutionVersionReported))
                {
                    return;
                }
                this.SuggestedActionsChanged?.Invoke(this, EventArgs.Empty);

                Volatile.Write(ref _lastSolutionVersionReported, solutionVersion);
            }

            public void Dispose()
            {
                if (_owner != null)
                {
                    var updateSource = (IDiagnosticUpdateSource)_owner._diagnosticService;
                    updateSource.DiagnosticsUpdated -= OnDiagnosticsUpdated;
                    _owner = null;
                }

                if (_workspace != null)
                {
                    _workspace.DocumentActiveContextChanged -= OnActiveContextChanged;
                    _workspace = null;
                }

                if (_registration != null)
                {
                    _registration.WorkspaceChanged -= OnWorkspaceChanged;
                    _registration = null;
                }

                if (_textView != null)
                {
                    _textView.Closed -= OnTextViewClosed;
                    _textView = null;
                }

                if (_subjectBuffer != null)
                {
                    _subjectBuffer = null;
                }
            }
        }
    }
}