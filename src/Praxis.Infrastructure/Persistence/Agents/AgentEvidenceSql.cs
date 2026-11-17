namespace Praxis.Infrastructure.Persistence.Agents;

/// <summary>Eligibility of a citation and its originating run, shared by recall and procedural learning.</summary>
internal static class AgentEvidenceSql
{
    internal const string ChangedCitation = """
        (NOT EXISTS (
            SELECT 1 FROM sources s JOIN items i ON i.source_id=s.source_id
            WHERE s.status='active' AND s.source_id=json_extract(citation.value,'$.sourceId')
              AND i.item_id=json_extract(citation.value,'$.itemId')
              AND json_extract(citation.value,'$.revision')=(SELECT MAX(v.revision) FROM item_versions v WHERE v.item_id=i.item_id))
        OR (json_extract(citation.value,'$.playbackSourceId') IS NOT NULL AND NOT EXISTS (
            SELECT 1 FROM sources media WHERE media.status='active' AND media.source_id=json_extract(citation.value,'$.playbackSourceId'))))
        """;

    internal const string CurrentRun = "NOT EXISTS(SELECT 1 FROM json_each(r.reply,'$.citations') citation WHERE " + ChangedCitation + ")" +
        " AND NOT EXISTS(SELECT 1 FROM agent_messages m,json_each(m.citations) citation WHERE m.conversation_id=r.conversation_id" +
        " AND m.sequence BETWEEN r.message_start AND r.message_end AND " + ChangedCitation + ")";
    internal const string CurrentSkill = "NOT EXISTS(SELECT 1 FROM agent_skill_experiences dependency JOIN agent_runs r ON r.run_id=dependency.run_id WHERE dependency.revision_id=skill.revision_id AND NOT (" + CurrentRun + "))";
}
