namespace DenChannels.Service.Channels;

// ChannelsRepository was split by task #2107. Focused repositories now own:
// - ChannelRepository: channels, messages, reactions, activity events, channel read cursors
// - MembershipRepository: channel memberships, agent-commons membership/brake, member discovery
// - WorkerPoolMembershipRepository: worker-pool lobby/control membership and active-work discovery
// - DirectConversationRepository: direct conversations and DM read cursors
// - ChannelProjectLinkRepository: channel/project links and linked-channel lookups
// - ChannelOverviewRepository: overview activity queries
