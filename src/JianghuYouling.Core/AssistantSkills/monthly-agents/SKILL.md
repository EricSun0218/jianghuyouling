---
name: monthly-agents
description: 过月同道主动行事与 AI 江湖事件
triggers: 过月,同道,队友,主动行事,江湖事件,江湖大事,纪事,月度,连载,失败,赴约,离队
---
# 过月 Agent

<!-- JHYL_ASSISTANT_KB_MONTHLY_AGENT_LOOP JHYL_ASSISTANT_KB_EVENT_DIRECT_FINAL_PROSE -->
<!-- JHYL_ASSISTANT_KB_MONTHLY_CHAT_UPGRADED_TO_ACTIONS JHYL_ASSISTANT_KB_NO_PROACTIVE_COMPAT JHYL_ASSISTANT_KB_COMPANION_MONTHLY_TOGGLE -->

过月同道与江湖事件都使用完整的多轮 Agent 循环：模型根据当前事实选择行动，逐次读取真实结果，再继续查询、换方案、采取后续行动或自然收束；不是程序随机拼动作，也不是先生成固定步骤计划。同道主动行事只保存和展示权威行为结果，不再要求模型另写正文；江湖事件仍由同一个 Agent 在行动完成后直接写出正文并进入纪事。

同道默认进入主动人物名单，但只是默认候选：玩家可在人物对话页点“移除主动”把同道排除，也可随时重新加入；聊过的普通人物也能手动加入或移除。名单同时约束主动来信与过月主动行事，但两项功能在设置中保持独立开关。每月从当前有效名单中稳定随机抽取设置数量，默认 3 人，设置为 0 时完全不触发，超过总数时按全部触发。同一存档同一月份恢复或重试不会换一批人。当前同道与太吾同行，不能自主离队、赴约、迁居或追杀到异地；玩家手动加入的普通人物则从自己当月的真实所在地行动，不会被伪装成队友或强拉到太吾身边。异地人物仍可通过千里传音联系；赠取、交换、偷窃、疗伤、授艺和写书交付等普通物理行为必须当面，下毒、擒拿、杀伤在过月语义中代表整月寻至目标后实施，由代码按目标状态与精纯裁决。人物只有在告知、请求、解释、谈判或表态确有需要时才自然交谈或传音，不再强制每月至少说一次话；讲话仍不计入三项、两类实质行为，代码会在派发瞬间按双方实时位置决定是当面告知还是千里传音。手动过月开关与人数在“过月”页，一键省钱/均衡/最佳体验在独立“体验档位”页。<!-- JHYL_ASSISTANT_KB_COMPANION_SAME_POOL JHYL_ASSISTANT_KB_COMPANION_CANDIDATE_FIX JHYL_COMPANION_MONTHLY_ALL_CURRENT_TEAMMATES -->

<!-- JHYL_ASSISTANT_KB_COMPANION_MONTHLY_ACTIONS JHYL_ASSISTANT_KB_COMPANION_MONTHLY_FULL_ACTION_CATALOG -->
同道可依据长期记忆、近期对话、人设、地点、时间和不含单向爱慕名单的显著关系网，进行赠物赠银、偷取第三方物品、疗伤、同地点任意两人以物换物、结交或解除关系、结仇化怨、当面告知或千里传音、下毒、擒拿、杀敌、增长良性品性、调整功法正逆练、回忆成书赠人、当面亲授、吐露秘闻、自主换装、门派说项、调整对具名人物的好感、改变任意人物的当前心情、根据公开事迹改变任意人物的名望、说媒和符合条件的春宵。同道不会替太吾消费、操纵太吾、发起本体战斗或主动改变外貌；梳头修面只由玩家在当面单聊中提出并转入本体互动。过月默认上下文不再预载“心系之人”；选定具体关系对象后仍由权威关系查询和动作前置校验。

明确失败会把引擎原因交回同一个 Agent，由其查询缺失事实、换对象、换物品或换行动继续思考；失败不会取消此前成功步骤，也不会直接让后续全部停止。只有回执终态未知时才停止可能重复的副作用。<!-- JHYL_ASSISTANT_KB_COMPANION_FAILURE_REPLAN -->

<!-- JHYL_ASSISTANT_KB_COMPANION_AGENT_LOOP JHYL_ASSISTANT_KB_COMPANION_RECEIPTS_ONLY -->
同道主动行事把全部真实工具结果写入长期记忆和纪事结果区，玩家根据这些连贯行为自行想象，不生成同道叙事正文。过月江湖事件可让事件人物使用几乎全部适合 NPC 对 NPC 或自身的行为，并可先查询人物、地点、物品、关系、技能和秘闻；不开放需要玩家确认的单聊战斗、替太吾消费或操纵太吾的玩家专属行为。每回江湖事件在写正文前至少执行 3 项真实非查询行动，并覆盖至少 2 类行为；赠物、赠银、交换、传授、写书和秘闻统一算“传递”，结交、结仇、好感与婚恋统一算“关系”，不能反复送礼、教功法或刷关系凑数。三项、两类只是不准提前结束的最低线，达到后仍由 Agent 继续核对最初欲望、承诺、冲突和未决线索；因果没有自然落定或被真实条件明确阻断时必须继续相关行动，不设动作次数额度。

事件地点优先使用太吾当前运行时区域，人物查询与区域人物池均以最新游戏状态为准。纪事弹窗会在任一内容生成后先显示，其他条目可显示生成中占位；左侧按时间导航，右侧展示所选月份的完整事件与同道行止。日志可用 JHYL_COMPANION_MONTHLY_TIMING 和 JHYL_MONTHLY_DIGEST_TIMING 分析候选、Agent 轮次、工具与总耗时。<!-- JHYL_ASSISTANT_KB_MONTHLY_FRONTEND_AREA JHYL_ASSISTANT_KB_MONTHLY_AREA_CHARS_FIX JHYL_ASSISTANT_KB_MONTHLY_TIMING_LOGS -->
