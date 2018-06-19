using Neo.Core;
using Neo.IO.Json;
using Neo.Properties;
using Neo.SmartContract;
using Neo.VM;
using Neo.Wallets;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace Neo.UI
{
    internal partial class InvokeContractDialog : Form
    {
        private InvocationTransaction tx;
        private JObject abi;
        private UInt160 script_hash;
        private ContractParameter[] parameters;
        private ContractParameter[] parameters_abi;

        private static readonly Fixed8 net_fee = Fixed8.FromDecimal(0.000m);

        public InvokeContractDialog(InvocationTransaction tx = null)
        {
            InitializeComponent();
            this.tx = tx;
            if (tx != null)
            {
                tabControl1.SelectedTab = tabPage2;
                textBox6.Text = tx.Script.ToHexString();
            }

            foreach (UInt256 asset_id in Program.CurrentWallet.FindUnspentCoins().Select(p => p.Output.AssetId).Distinct())
            {
                AssetState state = Blockchain.Default.GetAssetState(asset_id);
                cmbAsset.Items.Add(new Neo.Wallets.AssetDescriptor(asset_id));
            }

            foreach (string s in Settings.Default.NEP5Watched)
            {
                UInt160 asset_id = UInt160.Parse(s);
                try
                {
                    cmbAsset.Items.Add(new AssetDescriptor(asset_id));
                }
                catch (ArgumentException)
                {
                    continue;
                }
            }
        }

        public InvocationTransaction GetTransaction()
        {
            Fixed8 fee = tx.Gas.Equals(Fixed8.Zero) ? net_fee : Fixed8.Zero;
            return Program.CurrentWallet.MakeTransaction(new InvocationTransaction
            {
                Version = tx.Version,
                Script = tx.Script,
                Gas = tx.Gas,
                Attributes = tx.Attributes,
                Inputs = tx.Inputs,
                Outputs = tx.Outputs
            }, fee: fee);
        }

        public InvocationTransaction GetTransaction(UInt160 change_address, Fixed8 fee)
        {
            return Program.CurrentWallet.MakeTransaction(new InvocationTransaction
            {
                Version = tx.Version,
                Script = tx.Script,
                Gas = tx.Gas,
                Attributes = tx.Attributes,
                Inputs = tx.Inputs,
                Outputs = tx.Outputs
            }, change_address: change_address, fee: fee);
        }

        private void UpdateParameters()
        {
            parameters = new[]
            {
                new ContractParameter
                {
                    Type = ContractParameterType.String,
                    Value = comboBox1.SelectedItem
                },
                new ContractParameter
                {
                    Type = ContractParameterType.Array,
                    Value = parameters_abi
                }
            };
        }

        private void UpdateScript()
        {
            if (parameters.Any(p => p.Value == null)) return;
            using (ScriptBuilder sb = new ScriptBuilder())
            {
                sb.EmitAppCall(script_hash, parameters);
                textBox6.Text = sb.ToArray().ToHexString();
            }
        }

        private void textBox1_TextChanged(object sender, EventArgs e)
        {
            button1.Enabled = UInt160.TryParse(textBox1.Text, out _);
        }

        private void button1_Click(object sender, EventArgs e)
        {
            script_hash = UInt160.Parse(textBox1.Text);
            ContractState contract = Blockchain.Default.GetContract(script_hash);
            if (contract == null) return;
            parameters = contract.ParameterList.Select(p => new ContractParameter(p)).ToArray();
            textBox2.Text = contract.Name;
            textBox3.Text = contract.CodeVersion;
            textBox4.Text = contract.Author;
            textBox5.Text = string.Join(", ", contract.ParameterList);
            button2.Enabled = parameters.Length > 0;
            UpdateScript();
        }

        private void button2_Click(object sender, EventArgs e)
        {
            using (ParametersEditor dialog = new ParametersEditor(parameters))
            {
                dialog.ShowDialog();
            }
            UpdateScript();
        }

        private void textBox6_TextChanged(object sender, EventArgs e)
        {
            button3.Enabled = false;
            button5.Enabled = textBox6.TextLength > 0;
        }

        private static readonly byte[] GAS = { 231, 45, 40, 105, 121, 238, 108, 177, 183, 230, 93, 253, 223, 178, 227, 132, 16, 11, 141, 20, 142, 119, 88, 222, 66, 228, 22, 139, 113, 121, 44, 96 };

        private void button5_Click(object sender, EventArgs e)
        {
            byte[] script;
            try
            {
                script = textBox6.Text.Trim().HexToBytes();
            }
            catch (FormatException ex)
            {
                MessageBox.Show(ex.Message);
                return;
            }
            if (tx == null) tx = new InvocationTransaction();
            tx.Version = 1;
            tx.Script = script;

            if (tx.Attributes == null)
            {
                tx.Attributes = new TransactionAttribute[]
                {
                    //new TransactionAttribute{ Usage = TransactionAttributeUsage.Remark10, Data = Wallet.ToScriptHash(MainForm.selectedAddress).ToArray()},
                    //new TransactionAttribute{ Usage = TransactionAttributeUsage.Script, Data = UInt160.Parse(invokerScriptHash).ToArray()}
                };
            }

            if (tx.Inputs == null) tx.Inputs = new CoinReference[0];

            List<TransactionOutput> outputs = new List<TransactionOutput>();
            if (cmbAsset.SelectedIndex >= 0 && string.IsNullOrWhiteSpace(txtAssetQuantity.Text) == false)
            {
                AssetDescriptor asset = cmbAsset.SelectedItem as AssetDescriptor;
                outputs.Add(new TransactionOutput
                {
                    AssetId = asset.AssetId as UInt256,
                    ScriptHash = script_hash,
                    Value = new Fixed8((long)new BigDecimal(Fixed8.Parse(txtAssetQuantity.Text).GetData(), 8).Value)
                });
            }
            /*

            outputs.Add(new TransactionOutput
            {
                AssetId = new UInt256(GAS),
                ScriptHash = UInt160.Parse(invokerScriptHash),
                Value = Fixed8.FromDecimal(0.000m)
            });
            */

            tx.Outputs = outputs.ToArray();

            if (tx.Scripts == null) tx.Scripts = new Witness[0];
            ApplicationEngine engine = ApplicationEngine.Run(tx.Script, tx);
            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"VM State: {engine.State}");
            sb.AppendLine($"Gas Consumed: {engine.GasConsumed}");
            sb.AppendLine($"Evaluation Stack: {new JArray(engine.EvaluationStack.Select(p => p.ToParameter().ToJson()))}");
            textBox7.Text = sb.ToString();
            if (!engine.State.HasFlag(VMState.FAULT))
            {
                tx.Gas = engine.GasConsumed - Fixed8.FromDecimal(10);
                if (tx.Gas < Fixed8.Zero) tx.Gas = Fixed8.Zero;
                tx.Gas = tx.Gas.Ceiling();
                Fixed8 fee = tx.Gas.Equals(Fixed8.Zero) ? net_fee : tx.Gas;
                label7.Text = fee + " gas";
                button3.Enabled = true;
            }
            else
            {
                MessageBox.Show(Strings.ExecutionFailed);
            }
        }

        private void button6_Click(object sender, EventArgs e)
        {
            if (openFileDialog1.ShowDialog() != DialogResult.OK) return;
            textBox6.Text = File.ReadAllBytes(openFileDialog1.FileName).ToHexString();
        }

        private void button7_Click(object sender, EventArgs e)
        {
            if (openFileDialog2.ShowDialog() != DialogResult.OK) return;
            abi = JObject.Parse(File.ReadAllText(openFileDialog2.FileName));
            script_hash = UInt160.Parse(abi["hash"].AsString());
            textBox8.Text = script_hash.ToString();
            comboBox1.Items.Clear();
            comboBox1.Items.AddRange(((JArray)abi["functions"]).Select(p => p["name"].AsString()).ToArray());
            textBox9.Clear();
            button8.Enabled = false;
        }

        private void button8_Click(object sender, EventArgs e)
        {
            using (ParametersEditor dialog = new ParametersEditor(parameters_abi))
            {
                dialog.ShowDialog();
            }
            UpdateParameters();
            UpdateScript();
        }

        private void comboBox1_SelectedIndexChanged(object sender, EventArgs e)
        {
            string method = (string)comboBox1.SelectedItem;
            JArray functions = (JArray)abi["functions"];
            JObject function = functions.First(p => p["name"].AsString() == method);
            JArray _params = (JArray)function["parameters"];
            parameters_abi = _params.Select(p => new ContractParameter(p["type"].AsEnum<ContractParameterType>())).ToArray();
            textBox9.Text = string.Join(", ", _params.Select(p => p["name"].AsString()));
            button8.Enabled = parameters_abi.Length > 0;
            UpdateParameters();
            UpdateScript();
        }
    }
}
