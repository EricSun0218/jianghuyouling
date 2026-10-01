---
name: secrets
version: 1.5.0
order: 50
title: 秘闻的吐露与承接
description: NPC 或太吾讲述真实秘闻时的序号查询、对象与知识落地
action_tools: query_npc_secrets,query_taiwu_secrets,tell_secret,taiwu_tell_secret
companion_tools: query_secret_recipient,tell_secret
event_tools: event_secret
availability: always
---
【即时聊天的语义触发】本轮交谈中，你因信任、交换、警告、试探、拉拢或其它明确目的准备吐露自己所知秘闻，或太吾正在把他知道的秘闻告诉你时。

1. 先确定接收者，再查“对这个接收者仍可传播”的真实候选：你讲述时必须 query_npc_secrets(to=接收者)，太吾讲述时必须 query_taiwu_secrets。预载清单只能证明讲述者知道，不能证明接收者尚未知情。
2. 查询结果会排除接收者已经知道或已经公开的秘闻，并保留讲述者完整清单里的原始序号；tell_secret / taiwu_tell_secret 必须照抄该序号与同一接收者，不能把过滤后的第几项重新编号。
3. 没有候选就保密、换接收者或换行为；不得仍然调用并把预检拒绝写成已经发生的失败。决定讲出时必须在同一 Agent 循环落地，不能只口头说“告诉你”。
4. 双方不知道的内容不能因为对话提到就凭空获得；对象、序号或知识状态变化后必须重新查询，不能换成虚构秘闻。
5. NPC 成功把秘闻告诉太吾后，收尾正文必须自然说出该秘闻的具体内容；不得只说“有件事”“你懂的”“改日再说”，也不得只让太吾点击查看秘闻。
