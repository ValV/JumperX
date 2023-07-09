using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Sensors;

using static Platformer.Core.Simulation;
using Platformer.Gameplay;
using Platformer.Mechanics;
using Platformer.Model;
using System;
using System.Collections.Generic;
using System.Linq;

public class PlayerAgent : Agent {
    enum Reason {
        TIMEOUT,
        KILLED,
        FALLEN,
        STUCK,
        WON
    };
    Reason endReason = Reason.TIMEOUT;
    readonly string[] endReasonNames = {
        "timeout",
        "killed",
        "fallen",
        "stuck",
        "won"
    };
    public float TokenRewardMultiplier = 1.0f;
    public float EnemyRewardMultiplier = 1.0f;
    public float WinRewardMultiplier = 1.0f;
    public bool CurriculumEnabled = false;
    public int CurriculumWinThreshold = 10;
    // [SerializeField] readonly PlayerController player;
    readonly PlatformerModel model = GetModel<PlatformerModel>();
    Rigidbody2D body;

    bool jump;
    bool startJump = false;
    // bool startJumpDebug = false;
    bool stopJump;
    Vector2 move;

    bool facedEnemy = false;
    bool playerDead = false;

    float penaltyTick = 0.0f;
    const float penaltyTemporal = -1.0f;
    float penaltyEpisode;
    float penaltyTotal = 0.0f;
    float rewardEpisode = 0.0f;
    float rewardTokenCollect = 1.0e-5f;
    float rewardEnemyKill = 1.0e-5f;
    // float returnEpisode = 0.0f;
    float discountPositional = 0.0f;

    int numCollisions = 0;
    readonly int maxCollisions = 250;

    // List<ContactPoint2D> contacts;
    ContactPoint2D[] contacts;
    int numContacts = 0;
    ContactFilter2D filterContacts;

    float targetX = 0.0f;
    // float targetXReward = 0.0f;
    bool episodeActive = true;

    GameObject[] enemies;  // = new List<EnemyController>;
    Vector3[] enemyPositions;
    Vector2[] enemyVelocities;
    TokenInstance[] tokens;
    // TokenInstance[] tokensSource => FindObjectOfType<TokenController>().tokens;
    TokenController tokenController; // => model.FindObjectsOfType<TokenController>();
    GameController game => FindObjectOfType<GameController>();

    // internal int _CompletedEpisodes = 0;
    internal int _StepCount = 0;
    // internal int _EpisodeBeginCalled = 0;
    // internal int _StopEpisodeCalled = 0;
    // internal int _PlayerDeadCalled = 0;
    internal List<GameObject> curriculumSpawnPoints;
    internal int curriculumWinCount = 0;
    internal int curriculumSpawnIndex = 0;


    void Start() {
        // GameObject[] spwnpts = GameObject.FindGameObjectsWithTag("Respawn");
        // Debug.Log($"DEBUG: spawn points (array) = {spwnpts.Length}");
        curriculumSpawnPoints = GameObject.FindGameObjectsWithTag("Respawn").ToList();
        curriculumSpawnPoints.Sort(delegate(GameObject a, GameObject b) {
            return a.name.CompareTo(b.name);
        });
        if (CurriculumEnabled) {
            curriculumSpawnIndex = curriculumSpawnPoints.Count - 1;
        }
        // Debug.Log($"DEBUG: spawn index = {curriculumSpawnIndex}");
        model.spawnPoint = curriculumSpawnPoints[curriculumSpawnIndex].transform;
        model.player.Teleport(model.spawnPoint.transform.position);
        enemies = FindObjectsOfType<EnemyController>().ToList().ConvertAll(item => item.gameObject).ToArray();
        enemyPositions = new Vector3[enemies.Length];
        enemyVelocities = new Vector2[enemies.Length];
        for (int i = 0; i < enemies.Length; i ++) {
            enemyPositions[i] = enemies[i].transform.position + Vector3.zero;
            enemyVelocities[i] = enemies[i].GetComponent<EnemyController>().control.velocity + Vector2.zero;
        }
        // Debug.Log(string.Format("Number of enemies = {0}", enemies.Length));
        // var tokens_source = FindObjectOfType<TokenController>().tokens;
        // tokens = new TokenInstance[tokensSource.Length];
        // for (int i = 0; i < tokensSource.Length; i ++) {
        //     tokens[i] = tokensSource[i];
        // }
        // tokens = FindObjectsOfType<TokenInstance>();
        tokenController = game.GetComponent<TokenController>();
        tokens = new TokenInstance[tokenController.tokens.Length];
        for (int i = 0; i < tokens.Length; i++) {
            tokens[i] = tokenController.tokens[i];
        }
        // Debug.Log($"Num tokens = {tokens.Length}, num enemies = {enemies.Length}");
        float weightToken = 1.0f * tokens.Length / (tokens.Length + enemies.Length);
        // Debug.Log(string.Format("Token weight = {0}", weightToken));
        float weightEnemy = 1.0f - weightToken;
        // Debug.Log(string.Format("Enemy weight = {0}", weightEnemy));
        rewardTokenCollect = 1.0f / tokens.Length * weightToken * TokenRewardMultiplier;
        rewardEnemyKill = 1.0f / enemies.Length * weightEnemy * EnemyRewardMultiplier;
        // contacts = new List<ContactPoint2D>();
        contacts = new ContactPoint2D[8];
        filterContacts = new ContactFilter2D();
        filterContacts.SetLayerMask(LayerMask.GetMask("Level"));
        // filterContacts.SetNormalAngle(Mathf.PI * 1.25f, Mathf.PI * 1.75f);
        // filterContacts.useOutsideNormalAngle = true;
        // filterContacts.SetDepth(0.95f, 1.05f);
        body = model.player.GetComponent<Rigidbody2D>();
        penaltyTick = penaltyTemporal / MaxStep;
        penaltyEpisode = 0.0f;
        rewardEpisode = 0.0f;

        targetX = FindFirstObjectByType<VictoryZone>().transform.position.x;
        float power = Mathf.Pow(10, Mathf.Round(Mathf.Log10(targetX) - 1.0f));
        targetX = Mathf.Round(targetX / power) * power;

        // Debug.Log(string.Format("Victory zone target --> {0}", targetX));

        PlayerEnteredVictoryZone.OnExecute += PlayerEnteredVictoryZone_OnExecute;
        void PlayerEnteredVictoryZone_OnExecute(PlayerEnteredVictoryZone ev) {
            // Debug.Log(string.Format($"Ta-da!!! Game completed in {StepCount} steps (+{WinRewardMultiplier})"));
            // Game completed successfully
            endReason = Reason.WON;
            // Respawn at one of the spawn points
            curriculumWinCount ++;
            if (CurriculumEnabled) {
                if (curriculumWinCount >= CurriculumWinThreshold) {
                    curriculumWinCount = 0;
                    curriculumSpawnIndex --;
                    curriculumSpawnIndex = Math.Max(0, curriculumSpawnIndex);
                }
                model.spawnPoint = curriculumSpawnPoints[curriculumSpawnIndex].transform;
            }
            StopEpisode(1.0f);
            // --> EndEpisode
            Schedule<PlayerSpawn>(2);
        }

        PlayerEnteredDeathZone.OnExecute += PlayerEnteredDeathZone_OnExecute;
        void PlayerEnteredDeathZone_OnExecute(PlayerEnteredDeathZone ev) {
            // Debug.Log("Enter the domain of Death...");
            // Extra penalty for entering the Death Zone
            // AddReward(-0.25f);
            // rewardEpisode += -0.25f;
            // --> PlayerDeath
        }

        PlayerTokenCollision.OnExecute += PlayerTokenCollision_OnExecute;
        void PlayerTokenCollision_OnExecute(PlayerTokenCollision ev) {
            // Debug.Log(string.Format("Got it (+{0:0.0000})!", rewardTokenCollect));
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
            // Debug.Log(string.Format("Crush (+{0:0.0000})!", rewardEnemyKill));
            facedEnemy = false;
            // Reward for crushing enemies
            AddReward(rewardEnemyKill);  // <-- +0.5f
            rewardEpisode += rewardEnemyKill;
        }

        PlayerDeath.OnExecute += PlayerDeath_OnExecute;
        void PlayerDeath_OnExecute(PlayerDeath ev) {
            /// This function in practice is being called multiple time before respawn
            /// so guard episodes with 'episodeActive' variable which is reset on true respawn
            // _PlayerDeadCalled += 1;
            // There are many faces of Death, but you must face the only one
            if (!playerDead) {
                if (facedEnemy) {
                    // Debug.Log("You've been pwned (-1.0)!");
                    facedEnemy = false;
                    playerDead = true;
                    endReason = Reason.KILLED;
                } else {
                    // Debug.Log("You've died (-1.0)!");
                    playerDead = true;
                    endReason = Reason.FALLEN;
                }
            } else {
                // Now you're successfully dead
                if (episodeActive) {
                    StopEpisode(-1.0f);
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
                    // Debug.Log(string.Format("Enemy#{0} position = ({1}, {2}, {3})/({4}, {5}, {6}), " +
                    //                         "velocity = ({7}, {8})/({9}, {10})", i,
                    //                         enemy.transform.position.x, enemy.transform.position.y, enemy.transform.position.z,
                    //                         enemyPositions[i].x, enemyPositions[i].y, enemyPositions[i].z,
                    //                         enemy.control.velocity.x, enemy.control.velocity.y,
                    //                         enemyVelocities[i].x, enemyVelocities[i].y));
                }
        }

        PlayerSpawn.OnExecute += PlayerSpawn_OnExecute;
        void PlayerSpawn_OnExecute(PlayerSpawn ev) {
            // Debug.Log($"Spawn points = {curriculumSpawnPoints.Count}, spawn index = {curriculumSpawnIndex}");
            // foreach (var parameter in model.player.animator.parameters.Where(parameter => parameter.type == AnimatorControllerParameterType.Trigger)) {
            //     Debug.Log(parameter.name);
            // }
            // model.player.animator.SetBool("dead", false);
            // if (StepCount > 0) {
            //     // Here was the end of episode (not really smart)
            // }
            // List<EnemyController> enemies = new List<EnemyController>;
            // for (int i = 0; i < enemies.Length; i ++) {
            //     enemies[i]._collider.enabled = true;
            //     enemies[i].control.enabled = true;
            //     var health = enemies[i].GetComponent<Health>();
            //     if (!health.IsAlive) {
            //         health.Increment();
            //     }
            // };
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
                    //tokens[i].controller = tokenController;
                    tokenController.tokens[i] = tokens[i];
                    // tokenController.tokens[i] = tokens[i];
                    // tokensSource[i] = tokens[i];
                }

                // Reset enemies
                for (int i =0; i < enemies.Length; i ++) {
                    var enemy = enemies[i].GetComponent<EnemyController>();
                    enemy.GetComponent<Health>()?.Increment();
                    // enemy.control.gravityModifier = 0;
                    enemy._collider.enabled = true;
                    enemy.control.velocity = enemyVelocities[i] + Vector2.zero;
                    enemies[i].transform.position = enemyPositions[i] + Vector3.zero;
                    // enemies[i].GetComponent<EnemyController>().control.enabled = true;
                    // enemy.control.gravityModifier = 1;
                    // Debug.Log(string.Format("Enemy#{0} position = ({1}, {2}, {3})/({4}, {5}, {6}), " +
                    //                         "velocity = ({7}, {8})/({9}, {10})", i,
                    //                         enemy.transform.position.x, enemy.transform.position.y, enemy.transform.position.z,
                    //                         enemyPositions[i].x, enemyPositions[i].y, enemyPositions[i].z,
                    //                         enemy.control.velocity.x, enemy.control.velocity.y,
                    //                         enemyVelocities[i].x, enemyVelocities[i].y));
                }
            }
            playerDead = false;
            episodeActive = true;
            numCollisions = 0;
        }
    }

    void StopEpisode(float reward) {
        // _StopEpisodeCalled += 1;
        // Prepare and terminate episode
        // returnEpisode = rewardEpisode + penaltyEpisode;
        // AddReward(reward);
        // rewardEpisode += reward;
        // Discounted penalty - less penalty if agent is closer to Victory, more otherwise
        // reward = penaltyTemporal * (targetX - model.player.transform.position.x) / targetX - penaltyEpisode;
        discountPositional = Mathf.Clamp((targetX - model.player.transform.position.x) / targetX, 0.0f, 1.0f);
        penaltyTotal = -1 * discountPositional;
        if (reward < 0) {
            //
            AddReward(penaltyTotal - penaltyEpisode);  // dead: add residual penalty down to -1
        } else {
            AddReward(1.0f * WinRewardMultiplier);  // - rewardEpisode);  // win: add reward up to +1
            rewardEpisode += 1.0f * WinRewardMultiplier;
        }
        // reward = Mathf.Clamp(1.0f - (targetX - model.player.transform.position.x) / targetX, 0.0f, 1.0f);
        // AddReward(reward);
        // rewardEpisode += reward;
        // _CompletedEpisodes = CompletedEpisodes;
        // _StepCount = StepCount;
        // Debug.Log(string.Format("Stop episode: discount positional = {0}", discountPositional));
        episodeActive = false;
        EndEpisode();
    }

    void Update() {
        startJump = (startJump || Input.GetButtonDown("Jump")) && !Input.GetButtonUp("Jump"); 
    }

    void FixedUpdate() {
        // model.player.velocity = model.player.targetVelocity;  // <-- done in FixedUpdate in KinematicObject
        discountPositional = Mathf.Clamp((targetX - model.player.transform.position.x) / targetX, 0.0f, 1.0f);
        AddReward(penaltyTick * discountPositional);
        penaltyEpisode += penaltyTick * discountPositional;
        // rewardEpisode += penalty;
        _StepCount = StepCount;
    }

    public override void OnEpisodeBegin() {
        // _EpisodeBeginCalled += 1;
        // base.OnEpisodeBegin();
        // Reset variables as at the beginning of an episode
        // numCollisions = 0.0f;
        // Debug.Log($"BeginEpisode #{_EpisodeBeginCalled}, StopEpisode #{_StopEpisodeCalled}, PlayerDeath #{_PlayerDeadCalled}");
        // Debug.Log(string.Format("Stop episode: episodes played = {0}, step count = {1}",
        //                         CompletedEpisodes, _StepCount));
        string report = (
                $"Episode = {CompletedEpisodes}, steps = {_StepCount} ({endReasonNames[(int) endReason]})"
                + $", rewards = {rewardEpisode}, penalties = {penaltyEpisode}"
                + $", discount positional = {discountPositional}"
        );
        // if (penaltyTotal != 0) {
        if (endReason != Reason.TIMEOUT) {
            report += (
                // $"Episode = {CompletedEpisodes}, steps = {_StepCount}"
                // + $", rewards = {rewardEpisode}, penalties = {penaltyEpisode}"
                // + $", discount positional = {discountPositional}"
                $", penalty positional = {penaltyTotal}"
                + $", penalty residual = {penaltyTotal - penaltyEpisode}"
            );
        } else {
            // report += ;
        }
        Debug.Log(report);
        penaltyEpisode = 0.0f;  // reset timely penalty
        penaltyTotal = 0.0f;
        rewardEpisode = 0.0f;
        // returnEpisode = 0.0f;
        // if (!playerDead) {
        //     // Respawn if an episode ends with timeout
        //     // Schedule<PlayerSpawn>(0);
        // }
        endReason = Reason.TIMEOUT;
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
            // Debug.Log(string.Format("Move x = {0}, velocity x = {1}, target velocity x = {2}",
            //                         move.x, model.player.velocity.x, model.player.targetVelocity.x));
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
        // Debug.Log(string.Format("Player target velocity x = {0}, y = {1}",
        //                         model.player.targetVelocity.x, model.player.targetVelocity.y));

        // Punish agent if it runs into an obstacle
        if (body != null) {
            numContacts = body.GetContacts(filterContacts, contacts);
            for (int i = 0; i < numContacts; i ++) {
                if (Math.Abs(Math.Round(contacts[i].normal.x)) == 1) {
                    // Debug.Log(string.Format("Contact {0}, value = {1}, num contacts = {2}!", i,
                    //                         contacts[i].normal.x, numContacts));
                    numCollisions += 1;  // increment collision detector
                    break;
                }
                if (i == numContacts - 1) {
                    // Decrement collision detector
                    numCollisions = Math.Max(numCollisions - 1, 0);  // >= 0
                }
            }
            if (numCollisions >= maxCollisions && model.player.controlEnabled) {
                // Debug.Log("You're stuck!");
                endReason = Reason.STUCK;
                numCollisions = 0;
                model.player.controlEnabled = false;
                playerDead = true;
                StopEpisode(-1.0f);
                Schedule<PlayerSpawn>(0);
            } else {
            }
            // if (numContacts > 6) {
            //     // Debug.Log(string.Format("Number of contacts = {0}", numContacts));
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
