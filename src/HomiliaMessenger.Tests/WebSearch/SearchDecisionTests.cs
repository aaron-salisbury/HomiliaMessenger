using HomiliaMessenger.Business.Modules.Agents;
using HomiliaMessenger.Business.Modules.Conversations;
using HomiliaMessenger.Business.Modules.WebSearch;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;

namespace HomiliaMessenger.Tests.WebSearch;

[TestClass]
public sealed class SearchDecisionTests
{
    private static readonly DateOnly TODAY = new(2026, 11, 15);
    private static readonly string[] NAMES = ["Walt", "Walter Jones", "Aaron Salisbury"];

    [TestMethod]
    [DataRow("Can you look up when the next MonoGame release is?", "when the next MonoGame release is")]
    [DataRow("search for MonoGame 3.8.4 release notes", "MonoGame 3.8.4 release notes")]
    [DataRow("Could you check online whether Hollow Knight Silksong is on Switch?", "whether Hollow Knight Silksong is on Switch")]
    [DataRow("Avalonia 12 changelog, can you look it up?", "Avalonia 12 changelog")]
    [DataRow("What does the internet say about the Steam Deck 2?", "the Steam Deck 2")]
    public void Assess_SearchesWhenAskedTo(string message, string query)
    {
        SearchPlan plan = SearchDecision.Assess([User(message)], NAMES, TODAY);

        Assert.AreEqual(SearchNeed.Explicit, plan.Need);
        Assert.AreEqual(query, plan.Query);
    }

    [TestMethod]
    public void Assess_LooksUpWhatTheUserSaidBeforeWhenAskedToLookItUp()
    {
        SearchPlan plan = SearchDecision.Assess([User("Have you heard of Hollow Knight Silksong?"), Contact("Nope, what is it?"), User("Look it up!")], NAMES, TODAY);

        Assert.AreEqual(SearchNeed.Explicit, plan.Need);
        Assert.AreEqual("Hollow Knight Silksong", plan.Query);
    }

    [TestMethod]
    public void Assess_DoesNotSearchWhenThereIsNothingToLookUp()
    {
        SearchPlan plan = SearchDecision.Assess([User("Look it up for me please")], NAMES, TODAY);

        Assert.IsNull(plan.Query);
    }

    [TestMethod]
    [DataRow("Search for MonoGame. My password is hunter2.", "MonoGame")]
    [DataRow("Can you look up the Avalonia 12 changelog?\nAlso my PIN is 4321 and I live on Elm Street.", "the Avalonia 12 changelog")]
    [DataRow("I'm seeing Dr. Patel about my diagnosis. Anyway, search for MonoGame 3.8.4 release notes", "MonoGame 3.8.4 release notes")]
    public void Assess_SendsOnlyTheSentenceWithTheRequest(string message, string query)
    {
        SearchPlan plan = SearchDecision.Assess([User(message)], NAMES, TODAY);

        Assert.AreEqual(query, plan.Query);
    }

    [TestMethod]
    public void Assess_NeverTakesAnotherSentenceForTheSubject()
    {
        SearchPlan besideIt = SearchDecision.Assess([User("My password is hunter2. Can you look it up?")], NAMES, TODAY);
        SearchPlan earlierStatement = SearchDecision.Assess([User("My password is hunter2."), Contact("Ok?"), User("Look it up!")], NAMES, TODAY);

        Assert.IsNull(besideIt.Query);
        Assert.IsNull(earlierStatement.Query);
    }

    [TestMethod]
    [DataRow("I often search for HIV treatment online.")]
    [DataRow("I was going to look up my test results.")]
    [DataRow("I just look up recipes when I'm bored")]
    [DataRow("We searched for hours for my keys.")]
    [DataRow("Jen keeps googling her symptoms.")]
    [DataRow("Look up at the stars tonight!")]
    [DataRow("My therapist told me to search for a support group.")]
    public void Assess_DoesNotTakeStatementsAboutSearchingAsRequests(string message)
    {
        SearchPlan plan = SearchDecision.Assess([User(message)], NAMES, TODAY);

        Assert.AreEqual(SearchNeed.None, plan.Need);
    }

    [TestMethod]
    [DataRow("Please do not search online for my HIV diagnosis.")]
    [DataRow("Don't look up my test results, just tell me what you think.")]
    [DataRow("dont google it, is the new MonoGame version out yet?")]
    [DataRow("Never search the web for this: what's the latest on my lawsuit?")]
    [DataRow("No googling! Who won the election this week?")]
    [DataRow("Without looking it up, when does GTA 6 come out?")]
    [DataRow("Keep this off the internet, but is the new Zelda game out?")]
    public void Assess_NeverSearchesWhenTheUserSaysNotTo(string message)
    {
        SearchPlan plan = SearchDecision.Assess([User(message)], NAMES, TODAY);

        Assert.AreEqual(SearchNeed.None, plan.Need);
        Assert.IsNull(plan.Query);
    }

    [TestMethod]
    [DataRow("Is the new MonoGame version out yet?")]
    [DataRow("When does GTA 6 come out?")]
    [DataRow("What's the latest Zelda game?")]
    [DataRow("Did you see who won the election this week?")]
    [DataRow("Have you seen the trailer for the new Dune movie released last week?")]
    public void Assess_SearchesForCurrentInformation(string message)
    {
        SearchPlan plan = SearchDecision.Assess([User(message)], NAMES, TODAY);

        Assert.AreEqual(SearchNeed.Current, plan.Need);
        Assert.IsTrue(plan.TimeSensitive);
        Assert.IsNotNull(plan.Query);
    }

    [TestMethod]
    [DataRow("How are you today?")]
    [DataRow("I just started playing Silksong and it's great.")]
    [DataRow("I'm so tired, work was rough.")]
    [DataRow("What did you have for dinner?")]
    [DataRow("Do you remember the lake house?")]
    [DataRow("Did Mom call you yesterday?")]
    [DataRow("What are you up to this week?")]
    [DataRow("lol")]
    [DataRow("Are you free on Saturday?")]
    [DataRow("Walt, how was your Thanksgiving?")]
    [DataRow("I love the latest album, it's so good")]
    public void Assess_LeavesOrdinaryConversationAlone(string message)
    {
        SearchPlan plan = SearchDecision.Assess([User(message)], NAMES, TODAY);

        Assert.AreEqual(SearchNeed.None, plan.Need);
    }

    [TestMethod]
    public void Assess_OnlyDecidesForAMessageNotYetAnswered()
    {
        SearchPlan plan = SearchDecision.Assess([User("Can you look up MonoGame?"), Contact("Sure.")], NAMES, TODAY);

        Assert.AreEqual(SearchNeed.None, plan.Need);
    }

    [TestMethod]
    [DataRow("Have you played Hollow Knight?")]
    [DataRow("What do you think of Stardew Valley?")]
    public void Assess_LeavesNamedThingsWithoutRecencyToTheModel(string message)
    {
        SearchPlan plan = SearchDecision.Assess([User(message)], NAMES, TODAY);

        Assert.AreEqual(SearchNeed.Uncertain, plan.Need);
        Assert.IsFalse(plan.TimeSensitive);
    }

    [TestMethod]
    public void Assess_TreatsARecentYearAsTimeSensitive()
    {
        Assert.IsTrue(SearchDecision.IsTimeSensitive("best games of 2026", TODAY));
        Assert.IsTrue(SearchDecision.IsTimeSensitive("best games of 2025", TODAY));
        Assert.IsFalse(SearchDecision.IsTimeSensitive("best games of 1998", TODAY));
    }

    [TestMethod]
    public void BuildQuery_LeavesOutNamesContactDetailsAndNumbers()
    {
        string? query = SearchDecision.BuildQuery(
            "Hey Walt, can you look up Aaron's flight UA 1234 to Denver? My email is aaron@example.com, phone +1 (555) 123-4567, booking 98765432 https://airline.example/booking?id=7",
            NAMES);

        Assert.IsNotNull(query);
        Assert.DoesNotContain("Walt", query);
        Assert.DoesNotContain("Aaron", query);
        Assert.DoesNotContain("example.com", query);
        Assert.DoesNotContain("555", query);
        Assert.DoesNotContain("98765432", query);
        Assert.DoesNotContain("booking?id", query);
        Assert.DoesNotContain("My", query);
        Assert.Contains("Denver", query);
    }

    [TestMethod]
    public void Assess_SendsOnlyTheSentenceThatAsks()
    {
        string message = "My sister Jen is moving to Portland next month and I'm worried about her new job at the clinic. Anyway, is the new MonoGame release out yet?";

        SearchPlan plan = SearchDecision.Assess([User(message)], NAMES, TODAY);

        Assert.AreEqual(SearchNeed.Current, plan.Need);
        Assert.DoesNotContain("Jen", plan.Query!);
        Assert.DoesNotContain("Portland", plan.Query!);
        Assert.DoesNotContain("clinic", plan.Query!);
        Assert.Contains("MonoGame", plan.Query!);
    }

    [TestMethod]
    public void BuildQuery_IsShort()
    {
        string? query = SearchDecision.BuildQuery(string.Join(' ', System.Linq.Enumerable.Repeat("Avalonia", 40)), NAMES);

        Assert.IsNotNull(query);
        Assert.IsLessThanOrEqualTo(SearchDecision.MAX_QUERY_WORDS, query.Split(' ').Length);
        Assert.IsLessThanOrEqualTo(SearchDecision.MAX_QUERY_CHARACTERS, query.Length);
    }

    [TestMethod]
    [DataRow("YES", true)]
    [DataRow(" yes.", true)]
    [DataRow("Yes, a search would help", true)]
    [DataRow("NO", false)]
    [DataRow("Yesterday I said no", false)]
    [DataRow("I think so", false)]
    [DataRow("", false)]
    [DataRow(null, false)]
    public void IsYes_AcceptsOnlyAClearYes(string? answer, bool expected)
    {
        Assert.AreEqual(expected, SearchDecision.IsYes(answer));
    }

    [TestMethod]
    public void ModelRequest_IsSmallPreciseAndGivenOnlyTheLatestMessage()
    {
        AgentRequest request = SearchDecision.ModelRequest("Have you played\nHollow Knight?", "model-x", TODAY);

        Assert.IsTrue(request.Precise);
        Assert.HasCount(1, request.Turns);
        Assert.AreEqual("Have you played Hollow Knight?", request.Turns[0].Text);
        Assert.Contains("\n", (IEnumerable<string>)request.StopTexts!);
        Assert.Contains("YES or NO", request.Instructions);
    }

    private static ChatMessage User(string text)
    {
        return new ChatMessage { Id = MessageId.New(), Sender = MessageSender.User, Text = text };
    }

    private static ChatMessage Contact(string text)
    {
        return new ChatMessage { Id = MessageId.New(), Sender = MessageSender.Contact, Text = text };
    }
}
