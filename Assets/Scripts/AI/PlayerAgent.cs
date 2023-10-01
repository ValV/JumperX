using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Sensors;

using static Platformer.Core.Simulation;
using Platformer.Gameplay;
using Platformer.Mechanics;
using Platformer.Model;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

public class PlayerAgent : Agent {
    // Config and public fields
    [Serializable]
    class Config {
        // public Config() {
        //     // Default constructor
        // }
        public float RewardGetToken = 1.0f;
        public float RewardKillEnemy = 1.0f;
        public float RewardWin = 1.0f;
        public float PenaltyMaxStep = 1.0f;  // penalty for whole episode (max steps)
        public float PenaltyKilled = 1.0f;  // penalty for being killed by an enemy
        public float PenaltyFallen = 1.0f;  // penalty for fall down the edge
        public float PenaltyCollisions = 1.0f;  // penalty for colliding for certain ammount of times
        public int CollisionsMax = 150;  // maximum number of collisions with obstacles to end episode
        public bool CurriculumEnabled = false;
        public int CurriculumWins = 10;
        public int CurriculumFailures = 10;
        public uint DebugLevel = 0;
    }
    public float RewardGetToken = 1.0f;
    public float RewardKillEnemy = 1.0f;
    public float RewardWin = 1.0f;
    public float PenaltyMaxStep = 1.0f;  // penalty for whole episode (max steps)
    public float PenaltyKilled = 1.0f;  // penalty for being killed by an enemy
    public float PenaltyFallen = 1.0f;  // penalty for fall down the edge
    public float PenaltyCollisions = 1.0f;  // penalty for colliding for certain ammount of times
    public int CollisionsMax = 150;  // maximum number of collisions with obstacles to end episode
    public bool CurriculumEnabled = false;
    public int CurriculumWins = 10;
    public int CurriculumFailures = 10;
    public uint DebugLevel = 0;

    internal enum Reason {
        TIMEOUT,
        KILLED,
        FALLEN,
        STUCK,
        WON
    };
    internal Reason endReason = Reason.TIMEOUT;
    internal readonly string[] endReasonNames = {
        "timeout",
        "killed",
        "fallen",
        "stuck",
        "won"
    };
    // [SerializeField] readonly PlayerController player;
    internal readonly PlatformerModel model = GetModel<PlatformerModel>();
    internal Rigidbody2D body;

    // Player move and jump states
    internal bool jump;
    internal bool startJump = false;
    internal bool stopJump;
    internal Vector2 move;

    // Scene and Palyer states
    internal bool facedEnemy = false;
    internal bool playerDead = false;
    internal bool episodeActive = true;  // episode lock (avoid multiple episode ends per death)
    internal float targetX = 0.0f;  // victory point X-coordinate (calculated)
    internal int stepCount = 0;  // current episode steps (mirror ML Agents episode steps)


    internal float penaltyStepPositional = 0.0f;  // penalty per update tick (calculated)
    internal float penaltyStepsTotal = 0.0f;  // residual penalty (calculated)
    internal float penaltyResidual = 0.0f;  // total accumulated penalty per episode (calculated)
    internal float penaltyEpisodeEnd = 0.0f;  // final episode penalty (if not win)
    internal float rewardEpisode = 0.0f;  // total accumulated reward per episode (calculated)
    internal float rewardTokenCollect = 1.0e-5f;  // reward per collected token (calculated)
    internal float rewardEnemyKill = 1.0e-5f;  // reward per killed enemy (calculated)
    // internal float discountPositional = 0.0f;  // positional coefficient (calculated on Player's X position)

    // Collision detection
    internal int numCollisions = 0;  // accumulated number of collisions (calculated)
    // List<ContactPoint2D> contacts;
    internal ContactPoint2D[] contacts;  // contact points for collision detection
    internal int numContacts = 0;  // current collision number of contacts
    internal ContactFilter2D filterContacts;  // collision contacts filter (horizontal/vertical)

    // Enemies
    internal GameObject[] enemies;  // list of enemies (initialized on start)
    internal Vector3[] enemyPositions;  // list of enemies positions (initialized on start)
    internal Vector2[] enemyVelocities;  // list of enemies velocities (initialized on start)
    // Tokens
    internal TokenInstance[] tokens;  // list of tokens (initialized on start)
    // TokenInstance[] tokensSource => FindObjectOfType<TokenController>().tokens;
    internal TokenController tokenController; // => model.FindObjectsOfType<TokenController>();
    internal GameController Game => FindObjectOfType<GameController>();

    // Curriculum
    internal List<GameObject> curriculumSpawnPoints;  // list of spawn points (initialized on start)
    internal int curriculumWinCount = 0;  // episode win counter for curriculum (calculated)
    internal int curriculumFailCount = 0;  // episode fail counter for curriculum (calculated)
    internal int curriculumSpawnIndex = 0;  // current spawn point index (calculated)
    internal int curriculumSpawnDepth = 1;

    // Misc
    internal static System.Random random;

    private Config config = new Config();

    internal void LoadConfig(string fileName = "config.json") {
        var cfg = "";
        if (File.Exists(fileName)) {
            cfg = File.ReadAllText(fileName, Encoding.UTF8);
            JsonUtility.FromJsonOverwrite(cfg, config);
        }
        RewardGetToken = config.RewardGetToken;
        RewardKillEnemy = config.RewardKillEnemy;
        RewardWin = config.RewardWin;
        PenaltyMaxStep = config.PenaltyMaxStep;
        PenaltyKilled = config.PenaltyKilled;
        PenaltyFallen = config.PenaltyFallen;
        PenaltyCollisions = config.PenaltyCollisions;
        CollisionsMax = config.CollisionsMax;
        CurriculumEnabled = config.CurriculumEnabled;
        CurriculumWins = config.CurriculumWins;
        CurriculumFailures = config.CurriculumFailures;
        DebugLevel = config.DebugLevel;
        if (DebugLevel > 0 && cfg.Length > 2)
            Debug.Log($"Read config = {cfg}\n");
    }

    internal void SaveConfig(string fileName = "config.json") {
        config.RewardGetToken = RewardGetToken;
        config.RewardKillEnemy = RewardKillEnemy;
        config.RewardWin = RewardWin;
        config.PenaltyMaxStep = PenaltyMaxStep;
        config.PenaltyKilled = PenaltyKilled;
        config.PenaltyFallen = PenaltyFallen;
        config.PenaltyCollisions = PenaltyCollisions;
        config.CollisionsMax = CollisionsMax;
        config.CurriculumEnabled = CurriculumEnabled;
        config.CurriculumWins = CurriculumWins;
        config.CurriculumFailures = CurriculumFailures;
        config.DebugLevel = DebugLevel;
        var cfg = JsonUtility.ToJson(config);
        if (DebugLevel > 0)
            Debug.Log($"Save config = {cfg}\n");
        File.WriteAllText(fileName, cfg, Encoding.UTF8);
    }

    void Start() {
        LoadConfig();  // read parameters from config file
        random = new System.Random();
        curriculumSpawnPoints = GameObject.FindGameObjectsWithTag("Respawn").ToList();
        curriculumSpawnPoints.Sort(delegate(GameObject a, GameObject b) {
            return a.name.CompareTo(b.name);
        });
        if (CurriculumEnabled) {
            curriculumSpawnDepth = curriculumSpawnPoints.Count;
            curriculumSpawnIndex = curriculumSpawnDepth - 1;
        }
        model.spawnPoint = curriculumSpawnPoints[
            random.Next(curriculumSpawnIndex, curriculumSpawnDepth)
        ].transform;
        model.player.Teleport(model.spawnPoint.transform.position);
        enemies = FindObjectsOfType<EnemyController>().ToList().ConvertAll(item => item.gameObject).ToArray();
        enemyPositions = new Vector3[enemies.Length];
        enemyVelocities = new Vector2[enemies.Length];
        for (int i = 0; i < enemies.Length; i ++) {
            enemyPositions[i] = enemies[i].transform.position + Vector3.zero;
            enemyVelocities[i] = enemies[i].GetComponent<EnemyController>().control.velocity + Vector2.zero;
        }
        // var tokens_source = FindObjectOfType<TokenController>().tokens;
        // tokens = new TokenInstance[tokensSource.Length];
        // for (int i = 0; i < tokensSource.Length; i ++) {
        //     tokens[i] = tokensSource[i];
        // }
        // tokens = FindObjectsOfType<TokenInstance>();
        tokenController = Game.GetComponent<TokenController>();
        tokens = new TokenInstance[tokenController.tokens.Length];
        for (int i = 0; i < tokens.Length; i++) {
            tokens[i] = tokenController.tokens[i];
        }
        float weightToken = 1.0f * tokens.Length / (tokens.Length + enemies.Length);
        float weightEnemy = 1.0f - weightToken;
        rewardTokenCollect = RewardGetToken * weightToken / tokens.Length;
        rewardEnemyKill = RewardKillEnemy * weightEnemy / enemies.Length;
        // contacts = new List<ContactPoint2D>();
        contacts = new ContactPoint2D[8];
        filterContacts = new ContactFilter2D();
        filterContacts.SetLayerMask(LayerMask.GetMask("Level"));
        // filterContacts.SetNormalAngle(Mathf.PI * 1.25f, Mathf.PI * 1.75f);
        // filterContacts.useOutsideNormalAngle = true;
        // filterContacts.SetDepth(0.95f, 1.05f);
        body = model.player.GetComponent<Rigidbody2D>();
        penaltyStepPositional = (MaxStep > 0) ? -PenaltyMaxStep / MaxStep : 0;
        // penaltyEpisode = 0.0f;
        // rewardEpisode = 0.0f;

        targetX = FindFirstObjectByType<VictoryZone>().transform.position.x;
        float power = Mathf.Pow(10, Mathf.Round(Mathf.Log10(targetX) - 1.0f));
        targetX = Mathf.Round(targetX / power) * power;

        if (DebugLevel > 1) {
            Debug.Log($"Spawn index = {curriculumSpawnIndex}");
            Debug.Log($"Working directory = {Directory.GetCurrentDirectory()}");
            Debug.Log($"Num tokens = {tokens.Length}, num enemies = {enemies.Length}");
            Debug.Log($"Token weight = {weightToken}");
            Debug.Log($"Enemy weight = {weightEnemy}");
            Debug.Log($"Victory zone target = {targetX}");
        }

        PlayerEnteredVictoryZone.OnExecute += PlayerEnteredVictoryZone_OnExecute;
        void PlayerEnteredVictoryZone_OnExecute(PlayerEnteredVictoryZone ev) {
            if (DebugLevel > 1)
                Debug.Log(string.Format($"Ta-da!!! Game completed in {StepCount} steps (+{RewardWin})"));
            // Game completed successfully
            endReason = Reason.WON;
            // Respawn at one of the spawn points
            curriculumWinCount ++;
            if (CurriculumEnabled) {
                // Increase available spawn points if the agent is doing well
                if (curriculumWinCount >= CurriculumWins) {
                    curriculumWinCount = 0;
                    curriculumSpawnIndex --;
                    curriculumSpawnIndex = Math.Max(0, curriculumSpawnIndex);
                }
                model.spawnPoint = curriculumSpawnPoints[
                    random.Next(curriculumSpawnIndex, curriculumSpawnDepth)
                ].transform;
            }
            StopEpisode(RewardWin);
            // --> EndEpisode
            Schedule<PlayerSpawn>(2);
        }

        PlayerEnteredDeathZone.OnExecute += PlayerEnteredDeathZone_OnExecute;
        void PlayerEnteredDeathZone_OnExecute(PlayerEnteredDeathZone ev) {
            if (DebugLevel > 1)
                Debug.Log("Enter the domain of Death...");
            // Extra penalty for entering the Death Zone
            // AddReward(-0.25f);
            // rewardEpisode += -0.25f;
            // --> PlayerDeath
        }

        PlayerTokenCollision.OnExecute += PlayerTokenCollision_OnExecute;
        void PlayerTokenCollision_OnExecute(PlayerTokenCollision ev) {
            if (DebugLevel > 1)
                Debug.Log($"Got it (+{rewardTokenCollect:0.0000})!");
            // Reward for collecting tokens
            AddReward(rewardTokenCollect);  // <-- +0.5f
            rewardEpisode += rewardTokenCollect;
        }

        PlayerEnemyCollision.OnExecute += PlayerEnemyCollision_OnExecute;
        void PlayerEnemyCollision_OnExecute(PlayerEnemyCollision ev) {
            facedEnemy = true;
            // --> PlayerDeath | --> EnemyDeath
        }

        EnemyDeath.OnExecute += EnemyDeath_OnExecute;
        void EnemyDeath_OnExecute(EnemyDeath ev) {
            if (DebugLevel > 1)
                Debug.Log($"Crush (+{rewardEnemyKill:0.0000})!");
            facedEnemy = false;
            // Reward for crushing enemies
            AddReward(rewardEnemyKill);  // <-- +0.5f
            rewardEpisode += rewardEnemyKill;
        }

        PlayerDeath.OnExecute += PlayerDeath_OnExecute;
        void PlayerDeath_OnExecute(PlayerDeath ev) {
            /// This function in practice is being called multiple times before respawn
            /// so guard episodes with 'episodeActive' variable which is reset on true respawn
            // There are many faces of Death, but you must face the only one
            if (!playerDead) {
                if (facedEnemy) {
                    if (DebugLevel > 1)
                        Debug.Log($"You've been pwned (-{PenaltyKilled})!");
                    facedEnemy = false;
                    playerDead = true;
                    endReason = Reason.KILLED;
                } else {
                    if (DebugLevel > 1)
                        Debug.Log($"You've died (-{PenaltyFallen})!");
                    playerDead = true;
                    endReason = Reason.FALLEN;
                }
            } else {
                // Now you're successfully dead
                if (episodeActive) {
                    if (endReason == Reason.FALLEN) {
                        StopEpisode(-PenaltyFallen);  // -penalty = reward
                    } else {
                        StopEpisode(-PenaltyKilled);  // -penalty = reward
                    }
                }
                // --> EndEpisode
            }
        }

        EnablePlayerInput.OnExecute += EnablePlayerInput_OnExecute;
        void EnablePlayerInput_OnExecute(EnablePlayerInput ev) {
            // Enable controls for all the enemies
                for (int i =0; i < enemies.Length; i ++) {
                    var enemy = enemies[i].GetComponent<EnemyController>();
                    if (enemy._collider.enabled == true) {
                        enemy.control.enabled = true;
                    }
                }
        }

        PlayerSpawn.OnExecute += PlayerSpawn_OnExecute;
        void PlayerSpawn_OnExecute(PlayerSpawn ev) {
            // Reset environment variables
            if (true || playerDead) {
                // Reset tokens
                for (int i = 0; i < tokens.Length; i++) {
                    tokens[i].gameObject.SetActive(true);
                    tokens[i].collected = false;
                    if (tokens[i].randomAnimationStartTime) {
                        tokens[i].frame = UnityEngine.Random.Range(0, tokens[i].sprites.Length);
                    }
                    tokens[i].sprites = tokens[i].idleAnimation;
                    tokenController.tokens[i] = tokens[i];
                }

                // Reset enemies
                for (int i =0; i < enemies.Length; i ++) {
                    var enemy = enemies[i].GetComponent<EnemyController>();
                    enemy.GetComponent<Health>()?.Increment();
                    // enemy.control.gravityModifier = 0;
                    enemy._collider.enabled = true;
                    enemy.control.velocity = enemyVelocities[i] + Vector2.zero;
                    enemies[i].transform.position = enemyPositions[i] + Vector3.zero;
                }
            }
            playerDead = false;
            episodeActive = true;
            numCollisions = 0;
        }
    }

    private float GetPositionalCoefficient() {
        return Mathf.Clamp((targetX - model.player.transform.position.x) / targetX, 0.0f, 1.0f);
    }

    void StopEpisode(float reward) {
        // This method is not called on timeout!
        // Prepare and terminate episode
        // AddReward(reward);
        // rewardEpisode += reward;
        // Discounted penalty - less penalty if agent is closer to Victory, more otherwise
        // reward = penaltyTemporal * (targetX - model.player.transform.position.x) / targetX - penaltyEpisode;
        // discountPositional = ;
        penaltyResidual = (
            GetPositionalCoefficient() * penaltyStepPositional * MaxStep - penaltyStepsTotal
        );  // residual steps penalty
        switch (endReason) {
            case Reason.TIMEOUT: {
                break;  // stub (this method is not called on timeout)
            }
            case Reason.KILLED:
            case Reason.FALLEN:
            case Reason.STUCK: {
                AddReward(reward + penaltyResidual);  // fix agent's "suicide cheat"
                penaltyEpisodeEnd += reward;
                break;
            }
            case Reason.WON: {
                AddReward(reward);  // do not add residual penalty (bonus for speed)
                rewardEpisode += reward;
                break;
            }
            default: {
                break;
            }
        }
        // if (reward < 0) {
        //     // Negative rewards (penalties): <type of death> + <positional> + <episode steps>
        //     // Penalty happens on maximum steps reached, collision or death
        //     AddReward(penaltyResidual);  // dead: add residual penalty down to -1
        // } else {
        //     AddReward(1.0f * WinReward);  // - rewardEpisode);  // win: add reward up to +1
        //     rewardEpisode += 1.0f * WinReward;
        // }
        // // reward = Mathf.Clamp(1.0f - (targetX - model.player.transform.position.x) / targetX, 0.0f, 1.0f);
        // // AddReward(reward);
        // // rewardEpisode += reward;
        // // _StepCount = StepCount;
        // // Debug.Log(string.Format("Stop episode: discount positional = {0}", discountPositional));
        episodeActive = false;
        EndEpisode();
    }

    void Update() {
        startJump = (startJump || Input.GetButtonDown("Jump")) && !Input.GetButtonUp("Jump"); 
    }

    void FixedUpdate() {
        // model.player.velocity = model.player.targetVelocity;  // <-- done in FixedUpdate in KinematicObject
        // discountPositional = Mathf.Clamp((targetX - model.player.transform.position.x) / targetX, 0.0f, 1.0f);
        if (episodeActive) {
            float tick = GetPositionalCoefficient() * penaltyStepPositional * 1.0f;  // TODO: avoid initialization
            AddReward(tick);
            penaltyStepsTotal += tick;
        }
        // AddReward(penaltyTick * discountPositional);
        // penaltyEpisode += penaltyTick * discountPositional;
        // rewardEpisode += penalty;
        stepCount = StepCount;
    }

    public override void OnEpisodeBegin() {
        // Episode begins and ends where the player is at the moment,
        // after the begining of the episode, player is to be teleported to a spawn point
        // base.OnEpisodeBegin();
        // Reset variables as at the beginning of an episode (number of collisions are reset after)
        // numCollisions = 0.0f;  // goes after respawn
        if (DebugLevel > 1)
            Debug.Log($"Stop episode: episodes played = {CompletedEpisodes}");
        if (DebugLevel > 0) {
            string report = (
                    $"Episode = {CompletedEpisodes}"
                    + $", steps = {stepCount} ({endReasonNames[(int) endReason]})"
                    + $", rewards = {rewardEpisode}"
                    + $", penalties = {penaltyStepsTotal + penaltyResidual + penaltyEpisodeEnd}"
                    + $", positional = {GetPositionalCoefficient()}"
            );
            if (endReason != Reason.TIMEOUT) {
                report += (
                    $", penalty steps/residual/episode = {penaltyStepsTotal}/{penaltyResidual}"
                    + $"/{penaltyStepsTotal + penaltyResidual}"
                );
            }
            Debug.Log(report);
        }
        // SaveConfig();
        if (CurriculumEnabled && endReason != Reason.WON) {
            // Decrease available spawn points if the agent doing bad
            if (curriculumFailCount >= CurriculumFailures) {
                curriculumFailCount = 0;
                curriculumSpawnIndex ++;
                curriculumSpawnIndex = Math.Min(curriculumSpawnIndex, curriculumSpawnDepth - 1);
            }
            model.spawnPoint = curriculumSpawnPoints[
                random.Next(curriculumSpawnIndex, curriculumSpawnDepth)
            ].transform;
        }
        penaltyStepsTotal = 0.0f;  // reset accumulated steps penalty
        penaltyResidual = 0.0f;
        penaltyEpisodeEnd = 0.0f;
        rewardEpisode = 0.0f;
        endReason = Reason.TIMEOUT;  // set timeout as a default reason (here's no hook for it)
    }

    public override void CollectObservations(VectorSensor sensor) {
        // base.CollectObservations(sensor);
        // Observation is supposed to be a vector:
        // [0] position.x
        // [1] position.y
        // [2] velocity.x
        // [3] velocity.y
        // [4] jumpState
        // [5] controlEnabled
        sensor.AddObservation(transform.position.x);
        sensor.AddObservation(transform.position.y);
        sensor.AddObservation(model.player.velocity.x);
        sensor.AddObservation(model.player.velocity.y);
        sensor.AddObservation((float) model.player.jumpState);
        sensor.AddObservation(Convert.ToSingle(model.player.controlEnabled));
    }

    public override void Heuristic(float[] actionsOut) {
        // base.Heuristic(actionsOut);
        actionsOut[0] = Input.GetAxis("Horizontal");  // [-1, 1]
        actionsOut[1] = Mathf.Pow(-1.0f, 1.0f - Convert.ToSingle(startJump));
    }

    public override void OnActionReceived(float[] vectorAction) {
        // base.OnActionReceived(vectorAction);

        // <-- Controller.Update - user input counterpart in this.Update
        if (model.player.controlEnabled) {
            move.x = vectorAction[0];  // [-1, 1]
            jump = vectorAction[1] > 0;
            if (model.player.jumpState == PlayerController.JumpState.Grounded && jump) {
                model.player.jumpState = PlayerController.JumpState.PrepareToJump;
            } else if (!jump) {
                stopJump = true;
                Schedule<PlayerStopJump>().player = model.player;
            }
            if (DebugLevel > 2)
                Debug.Log(
                    $"Move x = {move.x}, velocity x = {model.player.velocity.x}"
                    + $", target velocity x = {model.player.targetVelocity.x}"
                );  // this will generate a lot of debug output
        } else {
            move.x = 0.0f;
        }

        // <-- Controller.UpdateJumpState
        jump = false;
        switch(model.player.jumpState) {
            case PlayerController.JumpState.PrepareToJump:
                model.player.jumpState = PlayerController.JumpState.Jumping;
                jump = true;
                stopJump = false;
                break;
            case PlayerController.JumpState.Jumping:
                if (!model.player.IsGrounded)
                {
                    Schedule<PlayerJumped>().player = model.player;
                    model.player.jumpState = PlayerController.JumpState.InFlight;
                }
                break;
            case PlayerController.JumpState.InFlight:
                if (model.player.IsGrounded)
                {
                    Schedule<PlayerLanded>().player = model.player;
                    model.player.jumpState = PlayerController.JumpState.Landed;
                }
                break;
            case PlayerController.JumpState.Landed:
                model.player.jumpState = PlayerController.JumpState.Grounded;
                break;
        }

        // <-- Controller.ComputeVelocity
        // <-- player.targetVelocity = Vector2.zero;
        if (jump && model.player.IsGrounded)
        {
            model.player.velocity.y = model.player.jumpTakeOffSpeed * model.jumpModifier;
            jump = false;
        }
        else if (stopJump)
        {
            stopJump = false;
            if (model.player.velocity.y > 0)
            {
                model.player.velocity.y *= model.jumpDeceleration;
            }
        }

        if (move.x > 0.01f)
            model.player.spriteRenderer.flipX = false;
        else if (move.x < -0.01f)
            model.player.spriteRenderer.flipX = true;

        model.player.animator.SetBool("grounded", model.player.IsGrounded);
        model.player.animator.SetFloat("velocityX", Mathf.Abs(model.player.velocity.x) / model.player.maxSpeed);

        model.player.targetVelocity = move * model.player.maxSpeed;
        if (DebugLevel > 2)
            Debug.Log(
                $"Player target velocity x = {model.player.targetVelocity.x}"
                + $", y = {model.player.targetVelocity.y}"
            );  // this will generate a lot of debug output

        // Punish agent if it runs into an obstacle
        if (body != null) {
            numContacts = body.GetContacts(filterContacts, contacts);
            for (int i = 0; i < numContacts; i ++) {
                if (Math.Abs(Math.Round(contacts[i].normal.x)) == 1) {
                    if (DebugLevel > 2)
                        Debug.Log(
                            $"Contact {i}, value = {contacts[i].normal.x}"
                            +$", num contacts = {numContacts}!"
                        );  // this will generate a lot of debug output
                    numCollisions += 1;  // increment collision detector
                    break;
                }
                if (i == numContacts - 1) {
                    // Decrement collision detector
                    numCollisions = Math.Max(numCollisions - 1, 0);  // >= 0
                }
            }
            if (numCollisions >= CollisionsMax && model.player.controlEnabled) {
                if (DebugLevel > 1)
                    Debug.Log("You're stuck!");
                endReason = Reason.STUCK;
                numCollisions = 0;
                model.player.controlEnabled = false;
                playerDead = true;
                StopEpisode(-PenaltyCollisions);
                Schedule<PlayerSpawn>(0);
            } else {
            }
            // if (numContacts > 6) {
            //     if (DebugLevel > 2)
            //         Debug.Log($"Number of contacts = {numContacts}");  // this will generate a lot of debug output
            //     numCollisions += 1;
            // } else {
            //     numCollisions = 0;
            // }
        }

        // Reward for speed (move - relative speed, player.targetVelocity - real speed)
        // Clip real speed with tanh function, shift down and scale
        // AddReward((float) (Math.Tanh(Mathf.Abs(model.player.targetVelocity.x) - 0.5) * 0.01));
        // Clip 50% relative speed, shift down by 10%, scale by 0.01 and use as a reward
        // AddReward((Mathf.Min(Mathf.Abs(move.x), 0.3f) - 0.1f) * 0.01f +
        //           (float) (model.player.targetVelocity.y * 0.01));
        // AddReward(transform.position.y * 0.01f + transform.position.x * 0.001f);
    }
}
