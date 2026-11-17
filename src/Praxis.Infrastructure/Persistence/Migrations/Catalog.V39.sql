CREATE VIRTUAL TABLE agent_recap_search USING fts5(conversation_id UNINDEXED, recap, tokenize='unicode61');

INSERT INTO agent_recap_search(conversation_id, recap)
SELECT conversation_id, recap FROM agent_chat_recaps WHERE recap IS NOT NULL;

CREATE TRIGGER agent_recap_search_insert AFTER INSERT ON agent_chat_recaps WHEN NEW.recap IS NOT NULL BEGIN
    INSERT INTO agent_recap_search(conversation_id, recap) VALUES(NEW.conversation_id, NEW.recap);
END;
CREATE TRIGGER agent_recap_search_update AFTER UPDATE OF recap ON agent_chat_recaps BEGIN
    DELETE FROM agent_recap_search WHERE conversation_id = OLD.conversation_id;
    INSERT INTO agent_recap_search(conversation_id, recap)
    SELECT NEW.conversation_id, NEW.recap WHERE NEW.recap IS NOT NULL;
END;
CREATE TRIGGER agent_recap_search_delete AFTER DELETE ON agent_chat_recaps BEGIN
    DELETE FROM agent_recap_search WHERE conversation_id = OLD.conversation_id;
END;

PRAGMA user_version = 39;
