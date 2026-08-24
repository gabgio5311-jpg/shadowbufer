using System;
using System.Collections;
using System.Collections.Generic;
using Modding;
using UnityEngine;
using USceneManager = UnityEngine.SceneManagement.SceneManager;

namespace HKMOD
{
    /// <summary>
    /// Ideia original:
    ///     if (Player gets the abyssal charm)
    ///         invocar sombras do abismo pelo mapa
    ///
    /// "Abyssal charm" = Void Heart (charm 36, o Kingsoul evoluido).
    /// Quando o jogador pega, as Shade Siblings do Abismo passam a brotar
    /// em qualquer sala de gameplay do jogo.
    /// </summary>
    public class AbyssShadows : Mod
    {
        // ---------------- Config ----------------

        /// <summary>PlayerData bool do Void Heart. Charm 36.</summary>
        private const string VoidHeartBool = "gotCharm_36";

        /// <summary>De onde o prefab da sombra e' puxado (preload da MAPI).</summary>
        private const string ShadeScene = "Abyss_09";
        private const string ShadeObject = "Siblings/Shade Sibling";

        /// <summary>Quantas sombras por sala.</summary>
        private const int ShadesPerRoom = 6;

        /// <summary>Deslocamento horizontal (em unidades de mundo) em relacao ao Knight.</summary>
        private const float SpawnRadiusMin = 6f;
        private const float SpawnRadiusMax = 16f;

        /// <summary>Segundos de espera depois da troca de sala, pro Knight ja' estar posicionado.</summary>
        private const float SpawnDelay = 1.5f;

        /// <summary>
        /// Quantos geo pequenos cada sombra larga ao morrer. Geo pequeno vale 1,
        /// medio vale 5 e grande vale 25 -- pro teste, 1 geo pequeno.
        /// </summary>
        private const int SmallGeoPerShade = 1;

        // ---------------- Estado ----------------

        internal static AbyssShadows Instance;

        private GameObject _shadePrefab;
        private readonly List<GameObject> _spawned = new List<GameObject>();
        private Coroutine _spawnRoutine;
        private bool _hasVoidHeart;

        /// <summary>Ultima cena em que a gente ja' tratou a entrada, pra nao spawnar duas vezes.</summary>
        private string _handledScene;

        /// <summary>Log unico do heartbeat, so' pra provar que o HeroUpdateHook esta' vivo.</summary>
        private bool _heroUpdateSeen;

        public AbyssShadows() : base("Abyss Shadows") { }

        public override string GetVersion() => "1.0.2-geo";

        // ---------------- Ciclo de vida da MAPI ----------------

        /// <summary>
        /// A MAPI carrega essas cenas em background antes do menu e entrega os objetos
        /// prontos no Initialize. E' a unica forma suportada de pegar um prefab de inimigo.
        /// </summary>
        public override List<(string, string)> GetPreloadNames()
        {
            return new List<(string, string)>
            {
                (ShadeScene, ShadeObject),
            };
        }

        public override void Initialize(Dictionary<string, Dictionary<string, GameObject>> preloaded)
        {
            Instance = this;

            _shadePrefab = TryGetPreloaded(preloaded, ShadeScene, ShadeObject);

            if (_shadePrefab == null)
            {
                LogError($"Preload falhou: nao achei '{ShadeObject}' na cena '{ShadeScene}'. " +
                         "O mod carregou mas nao vai spawnar nada. Confira o caminho na hierarquia da cena.");
            }
            else
            {
                // Tira da cena de preload (que e' descarregada) e mantem desligado como molde.
                _shadePrefab.SetActive(false);
                UnityEngine.Object.DontDestroyOnLoad(_shadePrefab);
                Log($"Prefab '{ShadeObject}' carregado de '{ShadeScene}'.");
            }

            ModHooks.SetPlayerBoolHook += OnSetPlayerBool;

            // Tres gatilhos independentes pra entrada de sala. O ModHooks.SceneChanged
            // sozinho nao estava disparando nesta instalacao, entao a gente cobre por
            // fora com o evento nativo do Unity e com o update do Knight.
            ModHooks.SceneChanged += OnModHooksSceneChanged;
            USceneManager.activeSceneChanged += OnUnitySceneChanged;
            ModHooks.HeroUpdateHook += OnHeroUpdate;

            Log("Abyss Shadows inicializado.");
        }

        private GameObject TryGetPreloaded(
            Dictionary<string, Dictionary<string, GameObject>> preloaded,
            string scene,
            string obj)
        {
            if (preloaded == null) return null;
            if (!preloaded.TryGetValue(scene, out var inScene) || inScene == null) return null;
            return inScene.TryGetValue(obj, out var go) ? go : null;
        }

        // ---------------- Hooks ----------------

        /// <summary>
        /// Roda toda vez que o jogo grava um bool no PlayerData. E' aqui que a gente
        /// pega o exato momento em que o Void Heart entra no inventario.
        /// Precisa devolver o valor (alterado ou nao) senao o jogo nao grava.
        /// </summary>
        private bool OnSetPlayerBool(string name, bool orig)
        {
            if (name == VoidHeartBool && orig && !_hasVoidHeart)
            {
                _hasVoidHeart = true;
                Log("Void Heart adquirido -- o Abismo comeca a vazar pelo mapa.");
                _handledScene = null;
                HandleSceneEnter("SetPlayerBool");
            }

            return orig;
        }

        private void OnModHooksSceneChanged(string sceneName)
        {
            Log($"[diag] gatilho: ModHooks.SceneChanged -> '{sceneName}'.");
            HandleSceneEnter("ModHooks.SceneChanged");
        }

        private void OnUnitySceneChanged(
            UnityEngine.SceneManagement.Scene from,
            UnityEngine.SceneManagement.Scene to)
        {
            Log($"[diag] gatilho: Unity.activeSceneChanged -> '{to.name}'.");
            HandleSceneEnter("Unity.activeSceneChanged");
        }

        /// <summary>Roda todo frame em que o Knight existe. Serve de rede de seguranca.</summary>
        private void OnHeroUpdate()
        {
            if (!_heroUpdateSeen)
            {
                _heroUpdateSeen = true;
                Log("[diag] gatilho: HeroUpdateHook esta vivo.");
            }

            HandleSceneEnter("HeroUpdateHook");
        }

        // ---------------- Spawn ----------------

        /// <summary>
        /// Ponto unico de entrada dos tres gatilhos. Deduplica por nome de cena, entao
        /// quem chegar primeiro leva e os outros viram no-op na mesma sala.
        /// </summary>
        private void HandleSceneEnter(string origem)
        {
            var gm = GameManager.instance;
            if (gm == null) return;

            string scene = gm.GetSceneNameString();
            if (string.IsNullOrEmpty(scene) || scene == _handledScene) return;

            _handledScene = scene;

            ClearSpawned();

            var pd = PlayerData.instance;
            if (pd == null)
            {
                Log($"[diag] entrada em '{scene}' (via {origem}): PlayerData.instance == null.");
                return;
            }

            // Cobre o caso de carregar um save que ja' tem o charm.
            _hasVoidHeart = pd.GetBool(VoidHeartBool);
            Log($"[diag] entrada em '{scene}' (via {origem}): voidHeart={_hasVoidHeart}, " +
                $"prefab={(_shadePrefab != null ? "ok" : "NULL")}, gameplay={gm.IsGameplayScene()}.");

            if (!_hasVoidHeart) return;

            RestartSpawning();
        }

        private void RestartSpawning()
        {
            var gm = GameManager.instance;
            if (gm == null)
            {
                Log("[diag] RestartSpawning: GameManager.instance == null.");
                return;
            }

            if (_shadePrefab == null)
            {
                Log("[diag] RestartSpawning: _shadePrefab virou null (destruido junto com a cena de preload?).");
                return;
            }

            if (_spawnRoutine != null)
            {
                gm.StopCoroutine(_spawnRoutine);
                _spawnRoutine = null;
            }

            _spawnRoutine = gm.StartCoroutine(SpawnRoutine());
            Log("[diag] RestartSpawning: coroutine agendada.");
        }

        private IEnumerator SpawnRoutine()
        {
            yield return new WaitForSeconds(SpawnDelay);

            var gm = GameManager.instance;

            // Menu, creditos, cutscene de titulo etc. nao levam sombra.
            if (gm == null || !gm.IsGameplayScene())
            {
                Log($"[diag] SpawnRoutine: abortou, IsGameplayScene={gm != null && gm.IsGameplayScene()}.");
                yield break;
            }

            var hero = HeroController.instance;
            if (hero == null)
            {
                Log("[diag] SpawnRoutine: HeroController.instance == null.");
                yield break;
            }

            Vector3 center = hero.transform.position;
            Log($"[diag] SpawnRoutine: rodando em '{gm.GetSceneNameString()}', hero em {center}.");

            for (int i = 0; i < ShadesPerRoom; i++)
            {
                SpawnOne(center);
                yield return new WaitForSeconds(0.2f);
            }

            _spawnRoutine = null;

            // Checagem tardia: quantas sobreviveram?
            yield return new WaitForSeconds(2f);

            int vivas = 0, ativas = 0;
            foreach (var go in _spawned)
            {
                if (go == null) continue;
                vivas++;
                if (go.activeInHierarchy) ativas++;
            }

            Log($"[diag] 2s depois: {vivas}/{_spawned.Count} ainda existem, {ativas} ativas na hierarquia.");
        }

        private void SpawnOne(Vector3 center)
        {
            // Jogo 2D: sortear um angulo joga metade das sombras pra dentro do teto ou
            // pro vazio. Desloca so' na horizontal e depois procura chao com raycast.
            float dx = UnityEngine.Random.Range(SpawnRadiusMin, SpawnRadiusMax);
            if (UnityEngine.Random.value < 0.5f) dx = -dx;

            var from = new Vector3(center.x + dx, center.y + 5f, center.z);
            var pos = new Vector3(center.x + dx, center.y, center.z);

            int terrain = LayerMask.GetMask("Terrain");
            var hit = Physics2D.Raycast(from, Vector2.down, 40f, terrain);

            if (hit.collider != null)
            {
                pos = new Vector3(hit.point.x, hit.point.y + 1.5f, center.z);
            }
            else
            {
                Log($"[diag] SpawnOne: sem chao abaixo de {from} (mask={terrain}), usando a altura do hero.");
            }

            var shade = UnityEngine.Object.Instantiate(_shadePrefab, pos, Quaternion.identity);
            shade.name = $"{ShadeObject} (Abyss Shadows)";
            shade.SetActive(true);

            _spawned.Add(shade);

            SetGeoDrop(shade);

            Log($"[diag] SpawnOne: instanciada em {pos}, activeInHierarchy={shade.activeInHierarchy}, " +
                $"componentes={shade.GetComponents<Component>().Length}");
        }

        /// <summary>
        /// Faz a sombra largar geo ao morrer. O HealthManager guarda a quantidade em
        /// tres campos (smallGeoDrops/mediumGeoDrops/largeGeoDrops) e o Die() cuida de
        /// cuspir as moedinhas. Shade Sibling normal nao dropa nada, entao a gente escreve.
        /// </summary>
        private void SetGeoDrop(GameObject shade)
        {
            var hm = shade.GetComponent<HealthManager>() ?? shade.GetComponentInChildren<HealthManager>();

            if (hm == null)
            {
                Log("[diag] geo: a sombra nao tem HealthManager -- nao vai dropar nada.");
                return;
            }

            // Os campos sao privados no jogo, entao vai por reflexao (a MAPI ja' cacheia).
            ReflectionHelper.SetField(hm, "smallGeoDrops", SmallGeoPerShade);
            ReflectionHelper.SetField(hm, "mediumGeoDrops", 0);
            ReflectionHelper.SetField(hm, "largeGeoDrops", 0);

            int conferido = ReflectionHelper.GetField<HealthManager, int>(hm, "smallGeoDrops");
            Log($"[diag] geo: HealthManager em '{hm.gameObject.name}', smallGeoDrops={conferido}.");
        }

        private void ClearSpawned()
        {
            foreach (var go in _spawned)
            {
                if (go != null) UnityEngine.Object.Destroy(go);
            }

            _spawned.Clear();
        }
    }
}
