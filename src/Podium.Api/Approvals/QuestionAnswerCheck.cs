using Podium.Api.Data;
using Podium.Api.Endpoints;
using Podium.Api.Text;
using Podium.Contracts;

namespace Podium.Api.Approvals;

/// <summary>
/// The owner's answers to the question tool (Q-2, F-335). Answers are accepted only for the question tool, only with an allow and only for the
/// questions the request really carries. A chosen answer must be one of the options the question offered (for a multiple choice, offered options
/// joined by ", "); anything else is refused, so the web cannot put its own words where the session reads a chosen option. Typed text (the "Other"
/// answer) comes in its own field and goes after the chosen options; the agent wraps it as a message from the web. A question that offered no
/// options (an older shape) takes its answer as typed text, as before.
/// </summary>
public static class QuestionAnswerCheck
{
    public const string InvalidAnswers = "invalid_answers";

    /// <summary>A chosen answer is not among the options the question offered.</summary>
    public const string InvalidAnswer = "invalid_answer";

    /// <summary>The refusal code, or null with the answers as the agent gets them (question text to chosen options, then typed text); null and null when none were sent.</summary>
    public static (string? Error, Dictionary<string, string>? Answers) Check(PermissionRequest request, string decision, Dictionary<string, string>? chosen,
        Dictionary<string, string>? typed)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (chosen is null && typed is null) return (null, null);
        if (request.ToolName != QuestionTools.AskUserQuestion || decision != PermissionDecisions.Allow) return (InvalidAnswers, null);
        var asked = QuestionAnswers.Offered(request.ToolInput.RootElement);
        IEnumerable<KeyValuePair<string, string>> all = [.. chosen ?? [], .. typed ?? []];
        var questions = all.Select(a => a.Key).Distinct(StringComparer.Ordinal).ToList();
        if (questions.Count is 0 or > CommandEndpoints.QuestionsMax) return (InvalidAnswers, null);
        if (!questions.All(asked.ContainsKey) || !all.All(a => Fits(a.Value))) return (InvalidAnswers, null);
        var answers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var q in questions)
        {
            var offered = asked[q];
            var pick = chosen?.GetValueOrDefault(q);
            // An offered question takes only its options as a chosen answer; one that offered none takes the answer as typed text.
            if (pick is not null && offered.Labels.Count > 0 && QuestionAnswers.ChosenLength(pick, offered) != pick.Length) return (InvalidAnswer, null);
            answers[q] = string.Join(QuestionAnswers.Separator, new[] { pick, typed?.GetValueOrDefault(q) }.Where(a => a is not null));
        }

        return (null, answers);
    }

    private static bool Fits(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= CommandEndpoints.AnswerMax && StorableText.Plain(value);
}
