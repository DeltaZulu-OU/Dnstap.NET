# FS-021: shared Go dnstap JSONL checks for all libfstrm receiver targets.
# The pinned Go reader renders DNS packet bytes as dig-like text.
length == $expected
and all(.[];
  .type == "MESSAGE"
  and .identity == "tapper"
  and .message.type == "CLIENT_QUERY"
  and .message.socket_protocol == "UDP"
  and .message.query_address == "192.0.2.10"
  and .message.response_port == 53
  and (.message.query_message | contains("example.com."))
)
and ((map(.message.query_message | capture("id: (?<id>[0-9]+)").id | tonumber) | sort)
     == [range(0; $expected)])
