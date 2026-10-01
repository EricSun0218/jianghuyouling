using System;
using System.Collections.Generic;

namespace JianghuYouling.Core.Behavior
{
    public enum MonthlyNoToolDecision
    {
        ContinueForMinimum,
        RequestCausalReview,
        EvaluateFinalNarrative,
    }

    /// <summary>
    /// Shared state machine for monthly agents when the model proposes a no-tool draft.
    /// The action/category floor only controls whether stopping may be considered. Crossing
    /// it always requires one later causal review; it never completes the run by itself.
    /// </summary>
    public sealed class MonthlyCausalCompletionGate
    {
        private bool _causalReviewIssued;

        public bool CausalReviewIssued => _causalReviewIssued;

        /// <summary>
        /// Marks that the model has already received an explicit causal-completion instruction
        /// after reading the latest authoritative action receipt.  Its next no-tool turn is
        /// therefore the model's own stop decision, not an automatic stop at the numeric floor.
        /// </summary>
        public void MarkReviewInstructionDelivered()
        {
            _causalReviewIssued = true;
        }

        public MonthlyNoToolDecision OnNoToolDraft(
            bool minimumSatisfied,
            bool explicitNoAction)
        {
            if (explicitNoAction)
                return MonthlyNoToolDecision.EvaluateFinalNarrative;
            if (!minimumSatisfied)
                return MonthlyNoToolDecision.ContinueForMinimum;
            if (!_causalReviewIssued)
            {
                _causalReviewIssued = true;
                return MonthlyNoToolDecision.RequestCausalReview;
            }
            return MonthlyNoToolDecision.EvaluateFinalNarrative;
        }
    }

    /// <summary>
    /// Production progress controller shared by event and companion monthly agent loops.
    /// It owns both acknowledged-action progress and the post-minimum causal-review state,
    /// preventing either caller from accidentally treating the numeric floor as completion.
    /// </summary>
    public sealed class MonthlyAgentCompletionState
    {
        private readonly int _minimumActions;
        private readonly int _minimumCategories;
        private readonly HashSet<string> _categories;
        private readonly MonthlyCausalCompletionGate _gate =
            new MonthlyCausalCompletionGate();

        public MonthlyAgentCompletionState(int minimumActions, int minimumCategories,
            int initialActions = 0, IEnumerable<string> initialCategories = null)
        {
            if (minimumActions < 1) throw new ArgumentOutOfRangeException(nameof(minimumActions));
            if (minimumCategories < 1)
                throw new ArgumentOutOfRangeException(nameof(minimumCategories));
            _minimumActions = minimumActions;
            _minimumCategories = minimumCategories;
            ActionCount = Math.Max(0, initialActions);
            _categories = new HashSet<string>(StringComparer.Ordinal);
            if (initialCategories != null)
                foreach (string category in initialCategories)
                    if (!string.IsNullOrWhiteSpace(category)) _categories.Add(category.Trim());
        }

        public int ActionCount { get; private set; }
        public ICollection<string> Categories => _categories;
        public bool MinimumSatisfied =>
            ActionCount >= _minimumActions && _categories.Count >= _minimumCategories;

        public void RecordSucceededAction(string category)
        {
            ActionCount++;
            if (!string.IsNullOrWhiteSpace(category)) _categories.Add(category.Trim());
        }

        public void MarkCausalReviewInstructionDelivered()
            => _gate.MarkReviewInstructionDelivered();

        public MonthlyNoToolDecision OnNoToolDraft(bool explicitNoAction)
            => _gate.OnNoToolDraft(MinimumSatisfied, explicitNoAction);
    }

    /// <summary>
    /// Detects post-minimum exploration that is no longer producing authoritative action
    /// receipts.  This is deliberately progress-based rather than action-count-based:
    /// every new successful action resets the fuse, so productive causal chains remain open.
    /// </summary>
    public sealed class MonthlyPostMinimumProgressFuse
    {
        private readonly int _nonProgressThreshold;

        public MonthlyPostMinimumProgressFuse(int nonProgressThreshold = 2)
        {
            if (nonProgressThreshold < 1)
                throw new ArgumentOutOfRangeException(nameof(nonProgressThreshold));
            _nonProgressThreshold = nonProgressThreshold;
        }

        public int ConsecutiveNonProgressRounds { get; private set; }

        public bool ObserveToolRound(bool minimumSatisfied, bool hasSuccessfulActionReceipt)
        {
            if (!minimumSatisfied || hasSuccessfulActionReceipt)
            {
                ConsecutiveNonProgressRounds = 0;
                return false;
            }
            ConsecutiveNonProgressRounds++;
            return ConsecutiveNonProgressRounds >= _nonProgressThreshold;
        }
    }

    /// <summary>
    /// Stops a monthly agent from spending the rest of its request budget on repeated queries,
    /// rejected calls or prose before it reaches the action floor.  This does not turn the
    /// numeric floor into an action quota: every successful real action resets the fuse and the
    /// post-minimum causal controller remains responsible for deciding when a productive chain
    /// is actually complete.
    /// </summary>
    public sealed class MonthlyPreMinimumProgressFuse
    {
        private readonly int _nonProgressThreshold;

        public MonthlyPreMinimumProgressFuse(int nonProgressThreshold = 3)
        {
            if (nonProgressThreshold < 1)
                throw new ArgumentOutOfRangeException(nameof(nonProgressThreshold));
            _nonProgressThreshold = nonProgressThreshold;
        }

        public int ConsecutiveNonProgressRounds { get; private set; }

        public bool ObserveRound(bool minimumSatisfied, bool hasSuccessfulActionReceipt)
        {
            if (minimumSatisfied || hasSuccessfulActionReceipt)
            {
                ConsecutiveNonProgressRounds = 0;
                return false;
            }
            ConsecutiveNonProgressRounds++;
            return ConsecutiveNonProgressRounds >= _nonProgressThreshold;
        }
    }
}
