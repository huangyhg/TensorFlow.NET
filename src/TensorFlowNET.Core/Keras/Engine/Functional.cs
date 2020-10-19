﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Tensorflow.Keras.ArgsDefinition;
using Tensorflow.Keras.Utils;
using static Tensorflow.Binding;

namespace Tensorflow.Keras.Engine
{
    /// <summary>
    /// A `Functional` model is a `Model` defined as a directed graph of layers.
    /// </summary>
    public class Functional : Model
    {
        TensorShape _build_input_shape;
        bool _compute_output_and_mask_jointly;
        bool _expects_training_arg;
        bool _expects_mask_arg;
        bool _autocast;
        List<Layer> _output_layers;
        List<Layer> _input_layers;
        List<KerasHistory> _input_coordinates;
        List<KerasHistory> _output_coordinates;
        public string[] NetworkNodes { get; set; }
        public Dictionary<int, List<Node>> NodesByDepth { get; set; }
        public List<Layer> Layers { get; set; }
        Dictionary<int, int> tensor_usage_count;
        public Dictionary<int, int> TensorUsageCount => tensor_usage_count;

        public Functional(Tensors inputs, Tensors outputs) 
            : base(new ModelArgs
            {
                Inputs = inputs,
                Outputs = outputs
            })
        {
            _input_layers = new List<Layer>();
            _output_layers = new List<Layer>();
            _input_coordinates = new List<KerasHistory>();
            _output_coordinates = new List<KerasHistory>();
            tensor_usage_count = new Dictionary<int, int>();
            _init_graph_network(inputs, outputs);
        }

        void _init_graph_network(Tensors inputs, Tensors outputs)
        {
            _is_graph_network = true;
            this.inputs = inputs;
            this.outputs = outputs;
            built = true;
            _build_input_shape = inputs.shape;
            _compute_output_and_mask_jointly = true;
            _expects_training_arg = true;
            _expects_mask_arg = true;
            // A graph network does not autocast inputs, as its layers will cast them instead.
            _autocast = false;

            if (outputs.Any(x => x.KerasHistory == null))
                base_layer_utils.create_keras_history(outputs);

            // Build self._output_layers:
            foreach (var x in outputs)
            {
                var (layer, node_index, tensor_index) = x.KerasHistory;
                _output_layers.append(layer);
                _output_coordinates.append(new KerasHistory(layer, node_index, tensor_index, x));
            }

            // Build self._input_layers:
            foreach(var x in inputs)
            {
                var (layer, node_index, tensor_index) = x.KerasHistory;
                _input_layers.append(layer);
                _input_coordinates.append(new KerasHistory(layer, node_index, tensor_index, x));
            }

            // Keep track of the network's nodes and layers.
            var (nodes, nodes_by_depth, layers, _) = MapGraphNetwork(inputs, outputs);

            NetworkNodes = nodes;
            NodesByDepth = nodes_by_depth;
            Layers = layers;

            ComputeTensorUsageCount();
        }

        void ComputeTensorUsageCount()
        {
            var available_tensors = inputs.Select(x => x.GetHashCode()).ToList();
            var depth_keys = NodesByDepth.Keys.Reverse().Skip(1).ToArray();
            foreach(var depth in depth_keys)
            {
                foreach(var node in NodesByDepth[depth])
                {
                    var input_tensors = node.KerasInputs.Select(x => x.GetHashCode()).ToArray();
                    if (input_tensors.issubset(available_tensors))
                    {
                        foreach (var tensor in node.KerasInputs)
                        {
                            if (!tensor_usage_count.ContainsKey(tensor.GetHashCode()))
                                tensor_usage_count[tensor.GetHashCode()] = 0;
                            tensor_usage_count[tensor.GetHashCode()] += 1;
                        }

                        foreach (var output_tensor in node.Outputs)
                            available_tensors.Add(output_tensor.GetHashCode());
                    }
                }
            }

            foreach (var tensor in outputs)
            {
                if (!tensor_usage_count.ContainsKey(tensor.GetHashCode()))
                    tensor_usage_count[tensor.GetHashCode()] = 0;
                tensor_usage_count[tensor.GetHashCode()] += 1;
            }
        }

        /// <summary>
        /// Validates a network's topology and gather its layers and nodes.
        /// </summary>
        /// <param name="inputs"></param>
        /// <param name="outputs"></param>
        (string[], Dictionary<int, List<Node>>, List<Layer>, Dictionary<int, List<Layer>>) MapGraphNetwork(Tensors inputs, Tensors outputs)
        {
            var (nodes_in_decreasing_depth, layer_indices) = BuildMap(outputs);
            var network_nodes = nodes_in_decreasing_depth
                .Select(node => MakeNodeKey(node.Layer.Name, node.Layer.InboundNodes.IndexOf(node)))
                .ToArray();

            var nodes_depths = new Dictionary<Node, int>();
            var layers_depths = new Dictionary<Layer, int>();

            nodes_in_decreasing_depth.Reverse();
            foreach (var node in nodes_in_decreasing_depth)
            {
                // If the depth is not set, the node has no outbound nodes (depth 0).
                int depth = nodes_depths.SetDefault(node, 0);
                // Update the depth of the corresponding layer
                int previous_depth = layers_depths.Get(node.Layer, 0);
                // If we've seen this layer before at a higher depth,
                // we should use that depth instead of the node depth.
                // This is necessary for shared layers that have inputs at different
                // depth levels in the graph.
                depth = Math.Max(depth, previous_depth);
                layers_depths[node.Layer] = depth;
                nodes_depths[node] = depth;

                // Update the depth of inbound nodes.
                // The "depth" of a node is the max of the depths
                // of all nodes it is connected to + 1.
                foreach(var node_dep in node.ParentNodes)
                {
                    previous_depth = nodes_depths.Get(node_dep, 0);
                    nodes_depths[node_dep] = Math.Max(depth + 1, previous_depth);
                }
            }

            // Handle inputs that are not connected to outputs.
            // We do not error out here because the inputs may be used to compute losses
            // and metrics.
            foreach(var input_t in inputs)
            {
                var (input_layer, _, _) = input_t.KerasHistory;
                if (!layers_depths.ContainsKey(input_layer))
                {
                    layers_depths[input_layer] = 0;
                    layer_indices[input_layer] = -1;
                    nodes_depths[input_layer.InboundNodes[0]] = 0;
                    network_nodes.add(MakeNodeKey(input_layer.Name, 0));
                }
            }

            // Build a dict {depth: list of nodes with this depth}
            var nodes_by_depth = new Dictionary<int, List<Node>>();
            foreach (var node in nodes_depths)
            {
                if (!nodes_by_depth.ContainsKey(node.Value))
                    nodes_by_depth[node.Value] = new List<Node>();
                nodes_by_depth[node.Value].append(node.Key);
            }

            var layers_by_depth = new Dictionary<int, List<Layer>>();
            foreach (var layer in layers_depths)
            {
                if (!layers_by_depth.ContainsKey(layer.Value))
                    layers_by_depth[layer.Value] = new List<Layer>();
                layers_by_depth[layer.Value].append(layer.Key);
            }

            // Get sorted list of layer depths.
            var depth_keys = layers_by_depth.Keys.Reverse();

            // Set self.layers ordered by depth.
            var layers = new List<Layer>();
            foreach(var depth in depth_keys)
            {
                var layers_for_depth = layers_by_depth[depth];

                // Network.layers needs to have a deterministic order:
                // here we order them by traversal order.
                layers_for_depth.Reverse();
                layers.AddRange(layers_for_depth);
            }

            // Get sorted list of node depths.
            depth_keys = nodes_by_depth.Keys.Reverse();

            return (network_nodes, nodes_by_depth, layers, layers_by_depth);
        }

        string MakeNodeKey(string layer_name, int node_index)
            => $"{layer_name}_ib-{node_index}";

        /// <summary>
        /// This method topologically sorts nodes in order from inputs to outputs.
        /// </summary>
        /// <param name="outputs"></param>
        (List<Node>, Dictionary<Layer, int>) BuildMap(Tensors outputs)
        {
            var finished_nodes = new List<Node>();
            var nodes_in_progress = new List<Node>();
            var nodes_in_decreasing_depth = new List<Node>();
            var layer_indices = new Dictionary<Layer, int>();
            foreach (var output in outputs)
                BuildMapHelper(output, 
                    finished_nodes, 
                    nodes_in_progress, 
                    nodes_in_decreasing_depth, 
                    layer_indices);

            return (nodes_in_decreasing_depth, layer_indices);
        }

        void BuildMapHelper(Tensor tensor, 
            List<Node> finished_nodes, 
            List<Node> nodes_in_progress,
            List<Node> nodes_in_decreasing_depth,
            Dictionary<Layer, int> layer_indices)
        {
            var (layer, node_index, _) = tensor.KerasHistory;
            var node = layer.InboundNodes[node_index];

            // Don't repeat work for shared subgraphs
            if (finished_nodes.Contains(node))
                return;

            // Prevent cycles.
            if (nodes_in_progress.Contains(node))
                throw new ValueError($"The tensor {tensor.name} at layer {layer.Name} is part of a cycle.");

            // Store the traversal order for layer sorting.
            if (!layer_indices.ContainsKey(layer))
                layer_indices[layer] = layer_indices.Count;

            // Propagate to all previous tensors connected to this node.
            nodes_in_progress.Add(node);
            foreach (var k_tensor in node.KerasInputs)
                BuildMapHelper(k_tensor,
                    finished_nodes,
                    nodes_in_progress,
                    nodes_in_decreasing_depth,
                    layer_indices);

            finished_nodes.Add(node);
            nodes_in_progress.Remove(node);
            nodes_in_decreasing_depth.Insert(nodes_in_decreasing_depth.Count, node);
        }

        protected override Tensors Call(Tensors inputs, Tensor state = null, bool is_training = false)
        {
            return run_internal_graph(inputs, is_training);
        }

        Tensors run_internal_graph(Tensors inputs, bool training = false, Tensors mask = null)
        {
            if (mask != null)
            {
                Tensor[] masks = new Tensor[inputs.Count()];
                foreach (var (i, input_t) in enumerate(inputs))
                    input_t.KerasMask = masks[i];
            }

            var tensor_dict = new Dictionary<int, Tensor[]>();
            foreach (var (x, y) in zip(this.inputs, inputs))
            {
                var y1 = conform_to_reference_input(y, x);
                var x_id = x.GetHashCode();
                tensor_dict[x_id] = Enumerable.Range(0, tensor_usage_count[x_id]).Select(x => y1).ToArray();
            }

            var depth_keys = NodesByDepth.Keys.Reverse().ToArray();

            foreach(var depth in depth_keys)
            {
                var nodes = NodesByDepth[depth];
                foreach(var node in nodes)
                {
                    // Input tensors already exist.
                    if (node.IsInput)
                        continue;

                    var layer_inputs = new Tensors(tensor_dict[node.FlatInputIds[0]]);
                    tensor_dict[node.FlatInputIds[0]] = new Tensor[0];

                    var outputs = node.Layer.Apply(layer_inputs, is_training: training);
                    
                    // Update tensor_dict.
                    foreach (var (x_id, y) in zip(node.FlatOutputIds, outputs))
                        tensor_dict[x_id] = Enumerable.Range(0, tensor_usage_count[x_id]).Select(x => y).ToArray();
                }
            }

            foreach(var x in outputs)
            {

            }
            throw new NotImplementedException("");
        }

        Tensor conform_to_reference_input(Tensor tensor, Tensor ref_input)
        {
            return tensor;
        }
    }
}