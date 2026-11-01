using HomiliaMessenger.Business.Modules.Agents;
using HomiliaMessenger.Business.Modules.Contacts;
using HomiliaMessenger.Business.Modules.Conversations;
using HomiliaMessenger.Business.Modules.Profile;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;

namespace HomiliaMessenger.Tests.Agents;

[TestClass]
public sealed class PersonaFramingTests
{
    private static readonly DateOnly TODAY = new(2026, 10, 31);

    private static readonly Contact WALT = new()
    {
        Id = ContactId.New(),
        GivenName = "Walt",
        FamilyName = "Kowalski",
        Nickname = "Grandpa Walt",
        Birthday = new DateOnly(1948, 3, 14),
        Gender = Gender.Male,
        Personality = "Gruff but warm.",
        Background = "Army medic, 1968."
    };

    [TestMethod]
    public void Instructions_DescribeThePersonaAndAskForATextInCharacter()
    {
        string instructions = PersonaFraming.Instructions(WALT, new UserProfile(), null, TODAY);

        Assert.Contains("You are Grandpa Walt, a real person texting with someone you know", instructions);
        Assert.Contains("Full name: Walt Kowalski", instructions);
        Assert.Contains("Age: 78 (born March 14, 1948)", instructions);
        Assert.Contains("Gender: Male", instructions);
        Assert.Contains("Personality: Gruff but warm.", instructions);
        Assert.Contains("Background: Army medic, 1968.", instructions);
        Assert.Contains("Today is Saturday, October 31, 2026.", instructions);
        Assert.Contains("never say or suggest that you are", instructions);
    }

    [TestMethod]
    public void Instructions_LeaveOutFieldsThatAreNotSet()
    {
        Contact rose = new() { Id = ContactId.New(), GivenName = "Rose", Birthday = new DateOnly(1990, 1, 1) };

        string instructions = PersonaFraming.Instructions(rose, new UserProfile(), null, TODAY);

        Assert.DoesNotContain("Full name", instructions);
        Assert.DoesNotContain("Personality", instructions);
        Assert.DoesNotContain("Background", instructions);
        Assert.DoesNotContain("the person you are texting", instructions);
        Assert.DoesNotContain("given who they are to you", instructions);
        Assert.DoesNotContain("What you remember", instructions);
    }

    [TestMethod]
    public void Instructions_TellThePersonaWhoTheUserIsAndHowTheyAreRelated()
    {
        UserProfile aaron = new() { Name = " Aaron ", Birthday = new DateOnly(1990, 5, 1), Gender = Gender.Male, About = "Lives in Denver. Nurse." };

        string instructions = PersonaFraming.Instructions(WALT with { Relationship = "Grandfather" }, aaron, null, TODAY);

        Assert.Contains("texting with Aaron in a messaging app", instructions);
        Assert.Contains("You are Aaron's grandfather.", instructions);
        Assert.Contains("About Aaron:\n- Age: 36\n- Gender: Male\n- About them: Lives in Denver. Nurse.".Replace("\n", Environment.NewLine), instructions);
        Assert.DoesNotContain("1990", instructions);
        Assert.Contains("Talk to Aaron the way you naturally would", instructions);

        // Who the user is comes before the persona's own details.
        Assert.IsLessThan(instructions.IndexOf("About you:", StringComparison.Ordinal), instructions.IndexOf("Aaron's grandfather", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("Grandfather", "You are the grandfather of the person you are texting.")]
    [DataRow("old college roommate", "You are the old college roommate of the person you are texting.")]
    [DataRow("BFF", "You are the BFF of the person you are texting.")]
    public void Instructions_StateTheRelationshipWithoutTheUsersName(string relationship, string expected)
    {
        string instructions = PersonaFraming.Instructions(WALT with { Relationship = relationship }, new UserProfile(), null, TODAY);

        Assert.Contains(expected, instructions);
    }

    [TestMethod]
    public void Memory_JoinsConsecutiveMessagesAndStartsWithTheUser()
    {
        List<ChatMessage> messages =
        [
            Message(MessageSender.Contact, "Are you coming Sunday?"),
            Message(MessageSender.User, "Yes"),
            Message(MessageSender.User, "Bringing pie"),
            Message(MessageSender.Contact, "Perfect"),
            Message(MessageSender.User, "What kind do you want?")
        ];

        IReadOnlyList<AgentTurn> turns = PersonaFraming.Memory(messages);

        Assert.AreSequenceEqual(
            new[]
            {
                new AgentTurn(MessageSender.User, "Yes\nBringing pie"),
                new AgentTurn(MessageSender.Contact, "Perfect"),
                new AgentTurn(MessageSender.User, "What kind do you want?")
            },
            turns.ToArray());
    }

    [TestMethod]
    public void Memory_KeepsOnlyTheMostRecentMessagesWithinTheBudget()
    {
        List<ChatMessage> messages = [];

        for (int i = 0; i < 100; i++)
        {
            messages.Add(Message(i % 2 == 0 ? MessageSender.User : MessageSender.Contact, $"{i}:" + new string('x', 400)));
        }

        IReadOnlyList<AgentTurn> turns = PersonaFraming.Memory(messages);

        Assert.IsLessThanOrEqualTo(PersonaFraming.MAX_MEMORY_CHARACTERS, turns.Sum(turn => turn.Text.Length));
        Assert.IsGreaterThan(3, turns.Count);
        Assert.StartsWith("99:", turns[^1].Text);
        Assert.AreEqual(MessageSender.User, turns[0].Sender);
    }

    [TestMethod]
    public void Memory_StartsOnTheSameMessageForSeveralReplies()
    {
        List<ChatMessage> messages = [];
        List<string> firstTurns = [];

        for (int i = 0; i < 60; i++)
        {
            messages.Add(Message(i % 2 == 0 ? MessageSender.User : MessageSender.Contact, $"{i}:" + new string('x', 200)));
            firstTurns.Add(PersonaFraming.Memory(messages)[0].Text);
        }

        // Messages of about 200 characters fill the budget after about 17; a start that slid with every message would
        // then change about 43 times.
        int changes = firstTurns.Zip(firstTurns.Skip(1)).Count(pair => pair.First != pair.Second);
        Assert.IsLessThanOrEqualTo(12, changes);
        Assert.IsGreaterThan(10, PersonaFraming.Memory(messages).Count);
    }

    [TestMethod]
    public void Memory_AlwaysIncludesTheLatestMessage()
    {
        List<ChatMessage> messages = [Message(MessageSender.User, "Short"), Message(MessageSender.User, new string('x', PersonaFraming.MAX_MEMORY_CHARACTERS + 1))];

        IReadOnlyList<AgentTurn> turns = PersonaFraming.Memory(messages);

        Assert.HasCount(1, turns);
        Assert.HasCount(PersonaFraming.MAX_MEMORY_CHARACTERS + 1, turns[0].Text);
    }

    [TestMethod]
    [DataRow("Still kicking.", "Still kicking.")]
    [DataRow("  Grandpa Walt: Still kicking.  ", "Still kicking.")]
    [DataRow("walt: Still kicking.", "Still kicking.")]
    [DataRow("\"Still kicking.\"", "Still kicking.")]
    [DataRow("Still kicking.\n\nHow's school?", "Still kicking.\n\nHow's school?")]
    [DataRow("Still kicking.\n\n(Note: This response keeps Walt's gruff tone.)", "Still kicking.")]
    [DataRow("Still kicking.\n\n*Note:* stays in character.\n\nMore notes.", "Still kicking.")]
    [DataRow("Still kicking.\r\n\r\nThis response maintains the tone.", "Still kicking.")]
    [DataRow("Still kicking.  ---  This dialogue keeps Walt gruff.", "Still kicking.  ---  This dialogue keeps Walt gruff.")]
    [DataRow("Still kicking.\n\n---\n\nThis dialogue keeps Walt gruff.", "Still kicking.")]
    [DataRow("Still kicking.\n---\nWalt answers briefly.", "Still kicking.")]
    [DataRow("Turkey's fine.\n\n#### End of Reply\n\nIn this response, Walt stays gruff.", "Turkey's fine.")]
    [DataRow("Turkey's fine.\n\nIn this reply, Walt stays gruff.", "Turkey's fine.")]
    [DataRow("I'm #1 at checkers.", "I'm #1 at checkers.")]
    public void CleanReply_KeepsOnlyThePersonasMessage(string raw, string expected)
    {
        Assert.AreEqual(expected, PersonaFraming.CleanReply(raw, WALT));
    }

    [TestMethod]
    public void Build_StopsTheReplyWhereTheModelStartsWritingTheNextTurns()
    {
        AgentRequest request = PersonaFraming.Build(WALT, new UserProfile { Name = "Aaron" }, null, [Message(MessageSender.User, "Hi")], "m", TODAY);

        string reply = PersonaFraming.CleanReply("Grandpa Walt: Doing fine, kiddo.  User: Great! Assistant: Anytime.", WALT, request)!;
        string named = PersonaFraming.CleanReply("Doing fine.\nAaron: Glad to hear it.\nGrandpa Walt: Yep.", WALT, request)!;

        Assert.AreEqual("Doing fine, kiddo.", reply);
        Assert.AreEqual("Doing fine.", named);
        Assert.AreEqual("Garden's fine.", PersonaFraming.CleanReply("Garden's fine.\n\nTomatoes came in late, too.", WALT, request));
        Assert.AreEqual(12, request.EndOf("Doing fine. <|end|>"));
        Assert.AreEqual(5, new AgentRequest("m", "i", []).EndOf("Hello"));
        Assert.AreEqual(0, request.EndOf(string.Empty));
        Assert.AreEqual("Hello there.", PersonaFraming.CleanReply("\n  Grandpa Walt: Hello there.\nAaron: Hi", WALT, request));
        Assert.AreEqual(20, request.EndOf("\n\nWalt: Hello there.\n\nMore"));
        Assert.IsNull(PersonaFraming.CleanReply("Grandpa Walt:", WALT, request));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("   ")]
    [DataRow("(Note: I would answer warmly.)")]
    public void CleanReply_ReturnsNullWhenNothingIsLeft(string? raw)
    {
        Assert.IsNull(PersonaFraming.CleanReply(raw, WALT));
    }

    private static ChatMessage Message(MessageSender sender, string text)
    {
        return new ChatMessage { Id = MessageId.New(), ConversationId = ConversationId.New(), Sender = sender, Text = text, SentAtUtc = DateTimeOffset.UtcNow };
    }
}
